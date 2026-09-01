using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class PlayerMediaTests
{
    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse(
            "11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ArtifactIdTag> ArtifactId =
        EntityId<ArtifactIdTag>.Parse(
            "22222222-2222-4222-8222-222222222222");

    private static readonly ContentDigest Digest =
        new(new string('a', 64));

    [TestMethod]
    public void MediaReference_IsCanonicalDigestBoundAndUsesReservedOrigin()
    {
        PlayerMediaAssetReference reference =
            PlayerMediaAssetReference.ProxyVideo(
                ProjectId,
                ArtifactId,
                Digest);
        string expected = string.Concat(
            "https://player-media.appassets.example/media/v1/",
            ProjectId.ToString(),
            "/proxy/",
            ArtifactId.ToString(),
            "/",
            Digest.Hex);

        Assert.AreEqual(expected, reference.Value);
        Assert.IsTrue(PlayerMediaAssetReference.TryParse(
            expected,
            out PlayerMediaAssetReference? parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(ProjectId, parsed.ProjectId);
        Assert.AreEqual(ArtifactId, parsed.ArtifactId);
        Assert.AreEqual(PlayerMediaAssetKind.ProxyVideo, parsed.Kind);
        Assert.AreEqual(Digest, parsed.ContentDigest);
        Assert.IsTrue(WebViewHost.IsAllowedMediaOrigin(expected));
        Assert.IsFalse(WebViewHost.IsAllowedOrigin(expected));

        PlayerMediaAssetReference thumbnail =
            PlayerMediaAssetReference.GeneratedThumbnail(
                ProjectId,
                ArtifactId,
                Digest);
        Assert.AreEqual(
            expected.Replace("/proxy/", "/thumbnail/", StringComparison.Ordinal),
            thumbnail.Value);
        Assert.IsTrue(PlayerMediaAssetReference.TryParse(
            thumbnail.Value,
            out PlayerMediaAssetReference? parsedThumbnail));
        Assert.AreEqual(
            PlayerMediaAssetKind.GeneratedThumbnail,
            parsedThumbnail!.Kind);
    }

    [TestMethod]
    public void MediaReference_RejectsNoncanonicalOrUnboundValues()
    {
        string valid = PlayerMediaAssetReference.ProxyVideo(
            ProjectId,
            ArtifactId,
            Digest).Value;
        string[] rejected =
        [
            valid.Replace(
                WebViewHost.MediaHostName,
                WebViewHost.VirtualHostName,
                StringComparison.Ordinal),
            valid.Replace("https://", "http://", StringComparison.Ordinal),
            valid.Replace("player-media", "PLAYER-MEDIA", StringComparison.Ordinal),
            valid.Replace("/proxy/", "/other/", StringComparison.Ordinal),
            valid.Replace(ArtifactId.ToString(), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            valid.Replace(Digest.Hex, Digest.Hex.ToUpperInvariant(), StringComparison.Ordinal),
            string.Concat(valid, "?range=private"),
            string.Concat(valid, "#fragment"),
            string.Concat(valid, "/"),
            valid.Replace("/media/v1/", "/media/", StringComparison.Ordinal),
            valid.Replace("/proxy/", "/proxy%2f", StringComparison.Ordinal),
            "file:///C:/private/proxy.mp4",
            @"C:\private\proxy.mp4",
            string.Empty,
        ];

        foreach (string value in rejected)
        {
            Assert.IsFalse(
                PlayerMediaAssetReference.TryParse(value, out _),
                value);
            Assert.IsFalse(WebViewHost.IsAllowedMediaAssetUri(value), value);
        }
    }

    [TestMethod]
    public async Task CreateAsync_WithoutRangeReturnsExactReadOnlyContent()
    {
        using MediaFixture fixture = new();
        ValidatedArtifactReadStream? opened = null;
        var reader = new StubAssetReader(reference =>
        {
            opened = fixture.Open(reference);
            return opened;
        });
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value,
            rangeHeader: null);

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("OK", response.ReasonPhrase);
        AssertHeader(response.Headers, "Content-Type: video/mp4");
        AssertHeader(response.Headers, $"Content-Length: {fixture.Content.Length}");
        AssertHeader(response.Headers, "Accept-Ranges: bytes");
        AssertHeader(response.Headers, "Cache-Control: no-store");
        AssertHeader(response.Headers, "X-Content-Type-Options: nosniff");
        AssertHeader(response.Headers, "Cross-Origin-Resource-Policy: same-site");
        Assert.IsFalse(response.Headers.Contains("Content-Range", StringComparison.Ordinal));
        Assert.AreEqual(1, reader.CallCount);
        Assert.AreEqual(fixture.Reference.Value, reader.LastReference?.Value);
        Stream body = response.Content
            ?? throw new AssertFailedException("The full media response has no body.");
        Assert.AreEqual(fixture.Content.LongLength, body.Length);
        Assert.AreEqual(0L, body.Position);
        Assert.IsTrue(body.CanRead);
        Assert.IsTrue(body.CanSeek);
        Assert.IsFalse(body.CanWrite);
        byte[] actual = new byte[fixture.Content.Length];
        await body.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(fixture.Content, actual);
        Assert.AreEqual(-1, body.ReadByte());
        Assert.ThrowsExactly<NotSupportedException>(
            () => body.Write(new byte[] { 42 }, 0, 1));

        response.Dispose();
        Assert.IsNotNull(opened);
        Assert.IsFalse(opened.CanRead);
    }

    [TestMethod]
    public async Task CreateAsync_GeneratedThumbnailReturnsOnlyDigestBoundPng()
    {
        using MediaFixture fixture = new();
        PlayerMediaAssetReference reference =
            PlayerMediaAssetReference.GeneratedThumbnail(
                ProjectId,
                ArtifactId,
                Digest);
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            reference.Value,
            rangeHeader: null);

        Assert.AreEqual(200, response.StatusCode);
        AssertHeader(response.Headers, "Content-Type: image/png");
        AssertHeader(response.Headers, $"Content-Length: {fixture.Content.Length}");
        Assert.AreEqual(reference.Value, reader.LastReference!.Value);
        Assert.IsFalse(response.Headers.Contains(
            @"C:\",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ArtifactReader_OpensR023BindingDigestPathWithOutputDigestReference()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            string.Concat(
                "HubRadio.ThumbnailMedia.",
                Guid.NewGuid().ToString("N")));
        try
        {
            string workspacePath = Path.Combine(rootPath, "workspace");
            Directory.CreateDirectory(workspacePath);
            string databasePath = Path.Combine(rootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(rootPath, "backups"));
            var createdAt = new DateTimeOffset(
                2041,
                2,
                3,
                4,
                5,
                6,
                TimeSpan.Zero);
            new ProjectStore(databasePath).CreateProject(
                new Project(
                    ProjectId,
                    "Generated thumbnail media",
                    "Verify opaque thumbnail media reads.",
                    "Test audience",
                    PublicationScope.InternalOnly,
                    deadline: null,
                    assets: [],
                    performers: []),
                "{\"project\":true}",
                createdAt);
            var store = new ArtifactStore(databasePath, workspacePath);
            byte[] content = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
            ContentDigest outputDigest = ContentDigest.Compute(content);
            ContentDigest bindingDigest = new(new string('b', 64));
            string stagingPath = Path.Combine(
                workspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "thumbnail.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, content);
            _ = await store.PromoteAsync(new ArtifactPromotionRequest(
                ArtifactId,
                ProjectId,
                ArtifactKind.Render,
                stagingPath,
                string.Concat(
                    "thumbnails/",
                    ArtifactId.ToString(),
                    "/",
                    bindingDigest.Hex,
                    ".png"),
                outputDigest,
                content.LongLength,
                createdAt));
            var reader = new ArtifactStorePlayerMediaAssetReader(store);
            PlayerMediaAssetReference reference =
                PlayerMediaAssetReference.GeneratedThumbnail(
                    ProjectId,
                    ArtifactId,
                    outputDigest);

            await using ValidatedArtifactReadStream? stream =
                await reader.OpenAsync(reference, CancellationToken.None);

            Assert.IsNotNull(stream);
            byte[] actual = new byte[content.Length];
            await stream.ReadExactlyAsync(actual);
            CollectionAssert.AreEqual(content, actual);
            Assert.IsNull(await reader.OpenAsync(
                PlayerMediaAssetReference.GeneratedThumbnail(
                    ProjectId,
                    ArtifactId,
                    new ContentDigest(new string('f', 64))),
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("bytes=2-5", 2, 4, "bytes 2-5/10")]
    [DataRow("bytes=6-", 6, 4, "bytes 6-9/10")]
    [DataRow("bytes=-3", 7, 3, "bytes 7-9/10")]
    [DataRow("bytes=0-999", 0, 10, "bytes 0-9/10")]
    public async Task CreateAsync_ServesOnlyTheResolvedSingleRange(
        string rangeHeader,
        int expectedStart,
        int expectedLength,
        string expectedContentRange)
    {
        using MediaFixture fixture = new();
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value,
            rangeHeader);

        Assert.AreEqual(206, response.StatusCode);
        Assert.AreEqual("Partial Content", response.ReasonPhrase);
        AssertHeader(response.Headers, "Content-Type: video/mp4");
        AssertHeader(response.Headers, $"Content-Length: {expectedLength}");
        AssertHeader(response.Headers, "Accept-Ranges: bytes");
        AssertHeader(
            response.Headers,
            $"Content-Range: {expectedContentRange}");
        Stream body = response.Content
            ?? throw new AssertFailedException("The partial media response has no body.");
        Assert.AreEqual(expectedLength, body.Length);
        byte[] actual = new byte[expectedLength];
        await body.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(
            fixture.Content[expectedStart..(expectedStart + expectedLength)],
            actual);
        Assert.AreEqual(-1, body.ReadByte());
        Assert.ThrowsExactly<IOException>(
            () => body.Seek(-1, SeekOrigin.Begin));
        Assert.ThrowsExactly<IOException>(
            () => body.Seek(1, SeekOrigin.End));
        Assert.AreEqual(0L, body.Seek(0, SeekOrigin.Begin));
        Assert.AreEqual(fixture.Content[expectedStart], body.ReadByte());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("bytes=")]
    [DataRow("bytes=-")]
    [DataRow("items=0-1")]
    [DataRow("bytes= 0-1")]
    [DataRow("bytes=0 -1")]
    [DataRow("bytes=0-1,3-4")]
    [DataRow("bytes=10-")]
    [DataRow("bytes=8-7")]
    [DataRow("bytes=-0")]
    [DataRow("bytes=9223372036854775808-")]
    public async Task CreateAsync_RejectsMalformedMultipleAndUnsatisfiableRanges(
        string rangeHeader)
    {
        using MediaFixture fixture = new();
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value,
            rangeHeader);

        Assert.AreEqual(416, response.StatusCode, rangeHeader);
        Assert.AreEqual("Range Not Satisfiable", response.ReasonPhrase);
        Assert.IsNull(response.Content);
        AssertHeader(response.Headers, "Content-Length: 0");
        AssertHeader(response.Headers, "Accept-Ranges: bytes");
        AssertHeader(response.Headers, "Content-Range: bytes */10");
        Assert.AreEqual(1, reader.CallCount);
    }

    [TestMethod]
    public async Task CreateAsync_RejectsUnsupportedMethodAndUnknownReferenceBeforeReading()
    {
        using MediaFixture fixture = new();
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse method = await factory.CreateAsync(
            "HEAD",
            fixture.Reference.Value,
            rangeHeader: null);
        using PlayerMediaResponse unknown = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value.Replace(
                "/proxy/",
                "/unknown/",
                StringComparison.Ordinal),
            rangeHeader: null);

        Assert.AreEqual(405, method.StatusCode);
        AssertHeader(method.Headers, "Allow: GET");
        Assert.IsNull(method.Content);
        Assert.AreEqual(404, unknown.StatusCode);
        Assert.IsNull(unknown.Content);
        Assert.AreEqual(0, reader.CallCount);
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("project")]
    [DataRow("kind")]
    [DataRow("digest")]
    [DataRow("size")]
    public async Task CreateAsync_RejectsAReaderResultWithMismatchedBinding(
        string mismatch)
    {
        using MediaFixture fixture = new();
        ValidatedArtifactReadStream? opened = null;
        var reader = new StubAssetReader(reference =>
        {
            opened = fixture.Open(
                reference,
                id: mismatch == "id"
                    ? EntityId<ArtifactIdTag>.New()
                    : null,
                projectId: mismatch == "project"
                    ? EntityId<ProjectIdTag>.New()
                    : null,
                kind: mismatch == "kind"
                    ? ArtifactKind.Preview
                    : null,
                digest: mismatch == "digest"
                    ? new ContentDigest(new string('f', 64))
                    : null,
                byteSize: mismatch == "size"
                    ? fixture.Content.LongLength + 1
                    : null);
            return opened;
        });
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value,
            rangeHeader: null);

        Assert.AreEqual(404, response.StatusCode);
        Assert.IsNull(response.Content);
        Assert.IsNotNull(opened);
        Assert.IsFalse(opened.CanRead);
    }

    [TestMethod]
    public async Task CreateAsync_ReaderFailureIsClosedAndDoesNotDiscloseDetails()
    {
        var reader = new ThrowingAssetReader();
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);
        string reference = PlayerMediaAssetReference.ProxyVideo(
            ProjectId,
            ArtifactId,
            Digest).Value;

        using PlayerMediaResponse response = await factory.CreateAsync(
            "GET",
            reference,
            rangeHeader: null);

        Assert.AreEqual(404, response.StatusCode);
        Assert.IsNull(response.Content);
        Assert.IsFalse(response.Headers.Contains(
            @"C:\private",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CreateAsync_PreCancelledRequestDoesNotReadAnArtifact()
    {
        using MediaFixture fixture = new();
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            AllowPlayerAccessAuthorization.Instance);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => factory.CreateAsync(
                    "GET",
                    fixture.Reference.Value,
                    rangeHeader: null,
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(0, reader.CallCount);
    }

    [TestMethod]
    public async Task CreateAsync_AuthorizationPrecedesReferenceAndArtifactLookup()
    {
        using MediaFixture fixture = new();
        var reader = new StubAssetReader(fixture.Open);
        var factory = new PlayerMediaResponseFactory(
            reader,
            DeniedPlayerAccessAuthorization.Instance);

        using PlayerMediaResponse validReference = await factory.CreateAsync(
            "GET",
            fixture.Reference.Value,
            rangeHeader: null);
        using PlayerMediaResponse invalidReference = await factory.CreateAsync(
            "GET",
            "https://player-media.appassets.example/not-an-asset",
            rangeHeader: null);

        Assert.AreEqual(404, validReference.StatusCode);
        Assert.AreEqual(404, invalidReference.StatusCode);
        Assert.IsNull(validReference.Content);
        Assert.IsNull(invalidReference.Content);
        Assert.AreEqual(0, reader.CallCount);
    }

    private static void AssertHeader(string headers, string expected)
    {
        CollectionAssert.Contains(
            headers.Split("\r\n", StringSplitOptions.None),
            expected);
    }

    private sealed class StubAssetReader(
        Func<PlayerMediaAssetReference, ValidatedArtifactReadStream?> open) :
        IPlayerMediaAssetReader
    {
        internal int CallCount { get; private set; }

        internal PlayerMediaAssetReference? LastReference { get; private set; }

        public Task<ValidatedArtifactReadStream?> OpenAsync(
            PlayerMediaAssetReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastReference = reference;
            return Task.FromResult(open(reference));
        }
    }

    private sealed class ThrowingAssetReader : IPlayerMediaAssetReader
    {
        public Task<ValidatedArtifactReadStream?> OpenAsync(
            PlayerMediaAssetReference reference,
            CancellationToken cancellationToken) =>
            throw new IOException(@"C:\private\project\proxy.mp4");
    }

    private sealed class AllowPlayerAccessAuthorization :
        IPlayerAccessAuthorization
    {
        internal static AllowPlayerAccessAuthorization Instance { get; } = new();

        public void DemandPreview()
        {
        }
    }

    private sealed class MediaFixture : IDisposable
    {
        private readonly string rootPath = Path.Combine(
            Path.GetTempPath(),
            string.Concat("HubRadio.PlayerMedia.", Guid.NewGuid().ToString("N")));
        private readonly string mediaPath;

        internal MediaFixture()
        {
            Directory.CreateDirectory(rootPath);
            mediaPath = Path.Combine(rootPath, "proxy.mp4");
            Content = Enumerable.Range(0, 10)
                .Select(static value => checked((byte)value))
                .ToArray();
            File.WriteAllBytes(mediaPath, Content);
            Reference = PlayerMediaAssetReference.ProxyVideo(
                ProjectId,
                ArtifactId,
                Digest);
        }

        internal byte[] Content { get; }

        internal PlayerMediaAssetReference Reference { get; }

        internal ValidatedArtifactReadStream Open(
            PlayerMediaAssetReference reference) => Open(
                reference,
                id: null,
                projectId: null,
                kind: null,
                digest: null,
                byteSize: null);

        internal ValidatedArtifactReadStream Open(
            PlayerMediaAssetReference reference,
            EntityId<ArtifactIdTag>? id,
            EntityId<ProjectIdTag>? projectId,
            ArtifactKind? kind,
            ContentDigest? digest,
            long? byteSize)
        {
            var artifact = new StoredArtifact(
                id ?? reference.ArtifactId,
                projectId ?? reference.ProjectId,
                kind ?? reference.Kind switch
                {
                    PlayerMediaAssetKind.ProxyVideo => ArtifactKind.Proxy,
                    PlayerMediaAssetKind.ThumbnailSprite => ArtifactKind.Preview,
                    PlayerMediaAssetKind.GeneratedThumbnail => ArtifactKind.Render,
                    _ => throw new ArgumentOutOfRangeException(nameof(reference)),
                },
                reference.Kind == PlayerMediaAssetKind.GeneratedThumbnail
                    ? string.Concat(
                        "projects/",
                        (projectId ?? reference.ProjectId).ToString(),
                        "/renders/thumbnails/",
                        (id ?? reference.ArtifactId).ToString(),
                        "/",
                        new string('b', 64),
                        ".png")
                    : string.Concat(
                        "projects/",
                        (projectId ?? reference.ProjectId).ToString(),
                        "/proxy/proxy.mp4"),
                digest ?? reference.ContentDigest,
                byteSize ?? Content.LongLength,
                new DateTimeOffset(
                    2037,
                    1,
                    2,
                    3,
                    4,
                    5,
                    TimeSpan.Zero));
            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            return new ValidatedArtifactReadStream(
                artifact,
                new FileStream(mediaPath, options));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // A failed assertion can keep a diagnostic handle alive until process exit.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed assertion can keep a diagnostic handle alive until process exit.
            }
        }
    }
}
