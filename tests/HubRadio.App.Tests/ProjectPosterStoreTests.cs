using HubRadio.App.Projects;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ProjectPosterStoreTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    // FR-053 AC-028.1: ffmpeg picks a representative frame (thumbnail filter) 1 s in, scaled to 640 px wide.
    [TestMethod]
    public void ArgumentsSeekOneSecondPickARepresentativeFrameAndScaleDown()
    {
        CollectionAssert.AreEqual(new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", "1", "-i", @"C:\in.mp4",
            "-frames:v", "1", "-vf", "thumbnail=8,scale=640:-2", "-q:v", "4", "-y", @"C:\out.jpg",
        }, ProjectPosterStore.BuildArguments(@"C:\in.mp4", @"C:\out.jpg", seek: true).ToArray());
        Assert.IsFalse(ProjectPosterStore.BuildArguments(@"C:\in.mp4", @"C:\out.jpg", seek: false).Contains("-ss"));
    }

    // FR-053 AC-028.1: a poster is written atomically and its digest is reported; a short clip retries without seeking.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CreateWritesThePosterAndRetriesFromTheFirstFrame(bool firstAttemptSucceeds)
    {
        using var workspace = new TemporaryWorkspace();
        var calls = new List<IReadOnlyList<string>>();
        var store = new ProjectPosterStore(workspace.Path, (request, _) =>
        {
            calls.Add(request.ArgumentList);
            bool succeed = firstAttemptSucceeds || calls.Count == 2;
            if (succeed) File.WriteAllBytes(request.ArgumentList[^1], Jpeg);
            return Task.FromResult(new ProcessRunResult(succeed ? 0 : 1, "", "", false, TimeSpan.Zero));
        });
        var draftId = EntityId<ProjectCreationDraftIdTag>.New();

        Assert.IsTrue(await store.CreateAsync(draftId, workspace.Source, workspace.Ffmpeg));

        Assert.HasCount(firstAttemptSucceeds ? 1 : 2, calls);
        Assert.AreEqual(ContentDigest.Compute(Jpeg), store.TryGetDigest(draftId));
        CollectionAssert.AreEqual(Jpeg, store.ReadValidated(draftId, ContentDigest.Compute(Jpeg)));
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(workspace.Path, "posters")));
    }

    // FR-053 AC-028.2: failures never throw, never leave staging files and never produce a poster.
    [TestMethod]
    public async Task FailedOrMissingInputsProduceNoPosterAndNoLeftovers()
    {
        using var workspace = new TemporaryWorkspace();
        var store = new ProjectPosterStore(workspace.Path, (request, _) =>
        {
            File.WriteAllBytes(request.ArgumentList[^1], Jpeg);
            return Task.FromResult(new ProcessRunResult(1, "", "error", false, TimeSpan.Zero));
        });
        var draftId = EntityId<ProjectCreationDraftIdTag>.New();

        Assert.IsFalse(await store.CreateAsync(draftId, workspace.Source, workspace.Ffmpeg));
        Assert.IsFalse(await store.CreateAsync(draftId, Path.Combine(workspace.Path, "missing.mp4"), workspace.Ffmpeg));
        Assert.IsFalse(await new ProjectPosterStore(workspace.Path, (_, _) => throw new IOException("start failed"))
            .CreateAsync(draftId, workspace.Source, workspace.Ffmpeg));

        Assert.IsNull(store.TryGetDigest(draftId));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(workspace.Path, "posters")));
    }

    // FR-053 AC-028.3: the media route serves only a listed Project's poster whose digest still matches.
    [TestMethod]
    public void MediaRouteServesOnlyAListedProjectsCurrentPoster()
    {
        using var workspace = new TemporaryWorkspace();
        var store = new ProjectPosterStore(workspace.Path);
        var draftId = EntityId<ProjectCreationDraftIdTag>.New();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "posters"));
        File.WriteAllBytes(store.PathFor(draftId), Jpeg);
        ContentDigest digest = ContentDigest.Compute(Jpeg);
        string uri = SourceOnlyMediaResponseFactory.PosterUri(draftId, digest);
        Assert.AreEqual($"https://player-media.appassets.example/source-only/poster/{draftId}/{digest.Hex}", uri);

        using (PlayerMediaResponse ok = SourceOnlyMediaResponseFactory.CreatePosterResponse(uri, store, _ => true))
        {
            Assert.AreEqual(200, ok.StatusCode);
            StringAssert.Contains(ok.Headers, "Content-Type: image/jpeg");
            StringAssert.Contains(ok.Headers, "Cache-Control: no-store");
            using var copy = new MemoryStream();
            ok.Content!.CopyTo(copy);
            CollectionAssert.AreEqual(Jpeg, copy.ToArray());
        }

        string[] rejected =
        [
            uri.ToUpperInvariant(), uri + "/", uri + "?x=1",
            SourceOnlyMediaResponseFactory.PosterUri(draftId, ContentDigest.Compute([9])),
            SourceOnlyMediaResponseFactory.PosterUri(EntityId<ProjectCreationDraftIdTag>.New(), digest),
            SourceOnlyMediaResponseFactory.PosterPrefix + "../" + draftId + "/" + digest.Hex,
        ];
        foreach (string bad in rejected)
        {
            using PlayerMediaResponse response = SourceOnlyMediaResponseFactory.CreatePosterResponse(bad, store, _ => true);
            Assert.AreEqual(404, response.StatusCode, bad);
            Assert.IsNull(response.Content);
        }

        using PlayerMediaResponse deleted = SourceOnlyMediaResponseFactory.CreatePosterResponse(uri, store, _ => false);
        Assert.AreEqual(404, deleted.StatusCode);
        using PlayerMediaResponse noStore = SourceOnlyMediaResponseFactory.CreatePosterResponse(uri, null, _ => true);
        Assert.AreEqual(404, noStore.StatusCode);

        File.WriteAllBytes(store.PathFor(draftId), [1, 2, 3]);
        using PlayerMediaResponse changed = SourceOnlyMediaResponseFactory.CreatePosterResponse(uri, store, _ => true);
        Assert.AreEqual(404, changed.StatusCode);

        store.Delete(draftId);
        Assert.IsNull(store.TryGetDigest(draftId));
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        internal TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hubradio-poster-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Source = System.IO.Path.Combine(Path, "source.mp4");
            Ffmpeg = System.IO.Path.Combine(Path, "ffmpeg.exe");
            File.WriteAllBytes(Source, [0]);
            File.WriteAllBytes(Ffmpeg, [0]);
        }

        internal string Path { get; }
        internal string Source { get; }
        internal string Ffmpeg { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
