using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class MusicCatalogScannerTests
{
    [TestMethod]
    public async Task MissingAndEmptyDirectoriesReturnTypedWarnings()
    {
        using Fixture fixture = new();
        string missing = Path.Combine(fixture.Root, "missing");
        MusicCatalog absent = await Scanner(missing).ScanAsync();
        MusicCatalog empty = await Scanner(fixture.Root).ScanAsync();

        Assert.AreEqual(MusicLibraryState.Absent, absent.State);
        CollectionAssert.AreEqual(
            new[] { MusicCatalogWarning.LibraryAbsent },
            absent.Warnings.ToArray());
        Assert.AreEqual(MusicLibraryState.Empty, empty.State);
        CollectionAssert.AreEqual(
            new[] { MusicCatalogWarning.LibraryEmpty },
            empty.Warnings.ToArray());
    }

    [TestMethod]
    public async Task TopLevelMp3OnlyProducesDigestOrderedPrivacySafeCatalog()
    {
        using Fixture fixture = new();
        byte[] first = [3, 2, 1];
        byte[] second = [1, 2, 3];
        fixture.Write("z.MP3", first);
        fixture.Write("a.mp3", second);
        fixture.Write("ignored.wav", [9]);
        string nested = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "nested.mp3"), [8]);

        MusicCatalog catalog = await Scanner(fixture.Root).ScanAsync();

        Assert.AreEqual(MusicLibraryState.Available, catalog.State);
        Assert.HasCount(2, catalog.Tracks);
        string[] expected =
        [
            ContentDigest.Compute(first).Hex,
            ContentDigest.Compute(second).Hex,
        ];
        Array.Sort(expected, StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            expected,
            catalog.Tracks.Select(static item => item.ContentDigest.Hex).ToArray());
        Assert.IsFalse(catalog.GetType().GetProperties().Any(static property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("FileName", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(typeof(MusicTrack).GetProperties().Any(static property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("FileName", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task DuplicateContentIsCollapsedWithExplicitWarning()
    {
        using Fixture fixture = new();
        fixture.Write("one.mp3", [1, 2, 3]);
        fixture.Write("two.mp3", [1, 2, 3]);

        MusicCatalog catalog = await Scanner(fixture.Root).ScanAsync();

        Assert.HasCount(1, catalog.Tracks);
        CollectionAssert.Contains(
            catalog.Warnings.ToArray(),
            MusicCatalogWarning.DuplicateContent);
    }

    [TestMethod]
    public async Task CorruptProbeIsSkippedAndNoTrackFallbackIsExplicit()
    {
        using Fixture fixture = new();
        fixture.Write("bad.mp3", [1]);
        MusicCatalogScanner scanner = new(
            fixture.Root,
            (_, _) => throw new InvalidOperationException("private decode detail"));

        MusicCatalog catalog = await scanner.ScanAsync();

        Assert.AreEqual(MusicLibraryState.NoEligibleTracks, catalog.State);
        CollectionAssert.Contains(
            catalog.Warnings.ToArray(),
            MusicCatalogWarning.TrackUnreadable);
        CollectionAssert.Contains(
            catalog.Warnings.ToArray(),
            MusicCatalogWarning.NoEligibleTracks);
        Assert.IsFalse(catalog.ToString()!.Contains("bad.mp3", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MutationBetweenHashesFailsClosedWithoutPublishingTrack()
    {
        using Fixture fixture = new();
        string path = fixture.Write("race.mp3", [1, 2, 3]);
        MusicCatalogScanner scanner = new(
            fixture.Root,
            (candidate, _) =>
            {
                File.WriteAllBytes(candidate, [4, 5, 6]);
                return Task.FromResult(Probe());
            });

        MusicCatalog catalog = await scanner.ScanAsync();

        Assert.HasCount(0, catalog.Tracks);
        CollectionAssert.Contains(
            catalog.Warnings.ToArray(),
            MusicCatalogWarning.SourceChangedDuringScan);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task LaterProbeMutationInvalidatesEarlierTrackBeforeCatalogPublication()
    {
        using Fixture fixture = new();
        byte[] original = [1, 2, 3];
        string firstPath = fixture.Write("a.mp3", original);
        fixture.Write("b.mp3", [4, 5, 6]);
        MusicCatalogScanner scanner = new(
            fixture.Root,
            (candidate, _) =>
            {
                if (string.Equals(
                    Path.GetFileName(candidate),
                    "b.mp3",
                    StringComparison.Ordinal))
                {
                    File.WriteAllBytes(firstPath, [7, 8, 9]);
                }

                return Task.FromResult(Probe());
            });

        MusicCatalog catalog = await scanner.ScanAsync();

        Assert.AreEqual(MusicLibraryState.Available, catalog.State);
        Assert.HasCount(1, catalog.Tracks);
        Assert.IsFalse(catalog.Tracks.Any(track =>
            track.ContentDigest == ContentDigest.Compute(original)));
        CollectionAssert.Contains(
            catalog.Warnings.ToArray(),
            MusicCatalogWarning.SourceChangedDuringScan);
    }

    [TestMethod]
    public void UnsafeRootShapesAndAdsAreRejectedWithoutPathDisclosure()
    {
        using Fixture fixture = new();
        string[] invalid =
        [
            "relative",
            @"\\server\share\Music",
            @"\\.\pipe\Music",
            Path.Combine(fixture.Root, "Music") + ":stream",
        ];
        foreach (string path in invalid)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                Scanner(path));
            Assert.IsFalse(exception.ToString().Contains(path, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ProbeRequestIsShellFreeBoundedAndParserAcceptsOneAudioStream()
    {
        using Fixture fixture = new();
        string input = fixture.Write("input.mp3", [1]);
        string executable = Path.Combine(fixture.Root, "ffprobe.exe");
        ProcessRunRequest request = MusicCatalogScanner.CreateProbeRequest(
            executable,
            input,
            TimeSpan.FromSeconds(5));
        MusicAudioProbeResult parsed = MusicCatalogScanner.ParseProbeResult(
            new ProcessRunResult(
                0,
                """
                {"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2,"duration":"4.5"}],"format":{"duration":"5.0"}}
                """,
                string.Empty,
                false,
                TimeSpan.Zero));

        Assert.AreEqual(input, request.ArgumentList[^1]);
        Assert.IsNull(request.StandardInput);
        Assert.AreEqual(256 * 1024, request.MaxCapturedBytes);
        Assert.AreEqual(4.5m, parsed.DurationSeconds);
        Assert.AreEqual("mp3", parsed.CodecName);
        Assert.AreEqual(48000, parsed.SampleRate);
        Assert.AreEqual(2, parsed.Channels);
    }

    [TestMethod]
    public void ProbeParserRejectsUnknownMetadataAndNonemptyEnvelopes()
    {
        string[] invalid =
        [
            """
            {"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}],"format":{"duration":"5.0"}}
            """,
            """
            {"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2,"private":"name"}],"format":{"duration":"5.0"}}
            """,
            """
            {"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2}],"format":{"duration":"5.0","filename":"private.mp3"}}
            """,
            """
            {"programs":[{}],"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2}],"format":{"duration":"5.0"}}
            """,
            """
            {"streams":[{"codec_type":"audio","codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2}],"format":{"duration":"5.0"}}
            """,
            """
            {"streams":[{"codec_type":"audio","codec_name":"mp�","sample_rate":"48000","channels":2}],"format":{"duration":"5.0"}}
            """,
        ];

        foreach (string json in invalid)
        {
            InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                MusicCatalogScanner.ParseProbeResult(new ProcessRunResult(
                    0,
                    json,
                    string.Empty,
                    false,
                    TimeSpan.Zero)));
            Assert.IsFalse(exception.ToString().Contains("private", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ScanDoesNotRenameDeleteOrChangeSourceAttributes()
    {
        using Fixture fixture = new();
        string path = fixture.Write("source.mp3", [1, 2, 3]);
        FileAttributes before = File.GetAttributes(path);

        MusicCatalog catalog = await Scanner(fixture.Root).ScanAsync();

        Assert.AreEqual(MusicLibraryState.Available, catalog.State);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(before, File.GetAttributes(path));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    private static MusicCatalogScanner Scanner(string root) =>
        new(root, (_, _) => Task.FromResult(Probe()));

    private static MusicAudioProbeResult Probe() => new(5m, "mp3", 48000, 2);

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.MusicCatalogScannerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Write(string name, byte[] content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, content);
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