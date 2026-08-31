using System.Text;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class PlayerBridgeTests
{
    private const string RequestId =
        "00112233-4455-6677-8899-aabbccddeeff";

    private const string ActorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private const string OtherActorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private static readonly EntityId<AssetIdTag> SourceAssetId =
        EntityId<AssetIdTag>.Parse(
            "11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse(
            "22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<JobIdTag> JobId =
        EntityId<JobIdTag>.Parse(
            "33333333-3333-4333-8333-3333333333ab");

    private static readonly EntityId<ArtifactIdTag> ProxyArtifactId =
        EntityId<ArtifactIdTag>.Parse(
            "44444444-4444-4444-8444-444444444444");

    private static readonly EntityId<ArtifactIdTag> WaveformArtifactId =
        EntityId<ArtifactIdTag>.Parse(
            "55555555-5555-4555-8555-555555555555");

    private static readonly EntityId<ArtifactIdTag> SpriteImageArtifactId =
        EntityId<ArtifactIdTag>.Parse(
            "66666666-6666-4666-8666-666666666666");

    private static readonly EntityId<ArtifactIdTag> SpriteIndexArtifactId =
        EntityId<ArtifactIdTag>.Parse(
            "77777777-7777-4777-8777-777777777777");

    private static readonly ContentDigest SourceContentDigest =
        new(new string('a', 64));

    private static readonly ContentDigest SourcePtsIndexDigest =
        new(new string('b', 64));

    private static readonly ContentDigest ProxyContentDigest =
        new(new string('c', 64));

    private static readonly ContentDigest SpriteImageDigest =
        new(new string('9', 64));

    [TestMethod]
    public async Task ReadResponse_PreservesExactDomainsNegativeSourceStartAndObservedPts()
    {
        PlayerBridgeReadResult result = CreateAvailableResult();
        var handler = new PlayerBridgeRequestHandler(
            new StubReader(result),
            AllowPlayerAccessAuthorization.Instance);
        var request = new BridgeRequest(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerReadMethod,
            JobId);

        string responseJson = await handler.HandleAsync(request);

        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxPlayerResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(responseJson));
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement root = document.RootElement;
        AssertExactKeys(root, "version", "requestId", "method", "result");
        Assert.AreEqual(BridgeProtocol.PlayerReadMethod, root
            .GetProperty("method")
            .GetString());
        JsonElement readResult = root.GetProperty("result");
        AssertExactKeys(readResult, "status", "snapshot");
        Assert.AreEqual("available", readResult.GetProperty("status").GetString());
        JsonElement snapshot = readResult.GetProperty("snapshot");
        AssertRational(
            snapshot.GetProperty("sourcePresentationStart"),
            Time(-1, 10));
        AssertRational(
            snapshot.GetProperty("sourcePresentationEnd"),
            Time(3, 20));
        AssertRational(
            snapshot.GetProperty("proxy").GetProperty("presentationEnd"),
            Time(7, 60));
        JsonElement frames = snapshot.GetProperty("frames");
        Assert.AreEqual(3, frames.GetArrayLength());
        AssertRational(
            frames[1].GetProperty("proxyPresentationTime"),
            Time(1001, 30000));
        AssertRational(
            frames[2].GetProperty("proxyPresentationTime"),
            Time(1, 12));
        AssertRational(
            frames[2].GetProperty("sourcePresentationTime"),
            Time(1, 20));
        Assert.AreEqual(
            JsonValueKind.Null,
            frames[2].GetProperty("outputPresentationTime").ValueKind);

        string proxyReference = snapshot
            .GetProperty("proxy")
            .GetProperty("mediaReference")
            .GetString()!;
        string spriteReference = snapshot
            .GetProperty("sprite")
            .GetProperty("imageReference")
            .GetString()!;
        Assert.IsTrue(WebViewHost.IsAllowedMediaAssetUri(proxyReference));
        Assert.IsTrue(WebViewHost.IsAllowedMediaAssetUri(spriteReference));
        Assert.IsFalse(responseJson.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains("file://", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LargeReadResponseUsesPlayerEnvelopeInsteadOfCompactJobLimit()
    {
        PlayerBridgeReadResult available = CreateReadResult(
            CreateEvidenceFixture(waveformChannelCount: 128));
        Assert.AreEqual(PlayerBridgeStatus.Available, available.Status);
        var handler = new PlayerBridgeRequestHandler(
            new StubReader(available),
            AllowPlayerAccessAuthorization.Instance);
        var request = new BridgeRequest(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerReadMethod,
            JobId);

        string responseJson = await handler.HandleAsync(request);

        Assert.IsGreaterThan(
            BridgeProtocol.MaxResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(responseJson));
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxPlayerResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(responseJson));
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(
            JsonValueKind.Object,
            result.GetProperty("snapshot").ValueKind);
    }

    [TestMethod]
    public void BoundedResponseWriterRejectsGrowthBeforeAdvancingPastItsLimit()
    {
        var writer = new PlayerBridgeJson.BoundedBufferWriter(4);
        Span<byte> destination = writer.GetSpan(4);
        destination[..4].Fill(0x2a);
        writer.Advance(4);

        Assert.AreEqual(4, writer.WrittenSpan.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            writer.GetMemory(1));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            writer.Advance(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            writer.GetSpan(-1));
    }

    [TestMethod]
    public async Task Seek_RoundTripsOnlyThroughEvidenceBoundProxyFrameMap()
    {
        PlayerBridgeReadResult available = CreateAvailableResult();
        PlayerBridgeSnapshot snapshot = available.Snapshot!;
        string message = CreateSeekRequest(
            snapshot.EvidenceDigest.Hex,
            proxyFrameSequence: 2);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(message));
        string maximumMessage = CreateSeekRequest(
            snapshot.EvidenceDigest.Hex,
            int.MaxValue);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(maximumMessage));
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            maximumMessage,
            out BridgeRequest? maximumRequest,
            out BridgeRequestRejection maximumRejection));
        Assert.AreEqual(BridgeRequestRejection.None, maximumRejection);
        Assert.AreEqual(int.MaxValue, maximumRequest!.ProxyFrameSequence);
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.None, rejection);
        Assert.IsNotNull(request);

        var handler = new PlayerBridgeRequestHandler(
            new StubReader(available),
            AllowPlayerAccessAuthorization.Instance);
        using JsonDocument response = JsonDocument.Parse(
            await handler.HandleAsync(request));
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        JsonElement position = result.GetProperty("position");
        Assert.AreEqual(2, position.GetProperty("proxyFrameSequence").GetInt32());
        AssertRational(
            position.GetProperty("proxyPresentationTime"),
            Time(1, 12));
        Assert.AreEqual(2, position.GetProperty("sourceFrameSequence").GetInt32());
        AssertRational(
            position.GetProperty("sourcePresentationTime"),
            Time(1, 20));
        Assert.AreEqual(
            JsonValueKind.Null,
            position.GetProperty("outputPresentationTime").ValueKind);

        BridgeRequest atExclusiveEnd = new(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerSeekMethod,
            JobId,
            snapshot.EvidenceDigest,
            snapshot.Frames.Count);
        using JsonDocument invalid = JsonDocument.Parse(
            await handler.HandleAsync(atExclusiveEnd));
        Assert.AreEqual(
            "invalidTime",
            invalid.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.AreEqual(
            JsonValueKind.Null,
            invalid.RootElement.GetProperty("result").GetProperty("position").ValueKind);
    }

    [TestMethod]
    public async Task Evidence_MissingStaleAndMismatchedIndicesRemainDistinct()
    {
        PlayerBridgeReadResult available = CreateAvailableResult();
        PlayerBridgeSnapshot snapshot = available.Snapshot!;
        PlayerBridgeReadResult missing = PlayerBridgeEvidence.CreateReadResult(
            frameMap: null,
            proxyPtsIndex: null,
            previewAssets: null,
            proxyMediaReference: null,
            spriteImageReference: null);
        Assert.AreEqual(PlayerBridgeStatus.Unavailable, missing.Status);

        PlayerEvidenceFixture fixture = CreateEvidenceFixture();
        PlayerBridgeReadResult stale = PlayerBridgeEvidence.CreateReadResult(
            fixture.FrameMap,
            fixture.ProxyPtsIndex,
            fixture.PreviewAssets,
            PlayerMediaAssetReference.ProxyVideo(
                ProjectId,
                ProxyArtifactId,
                ProxyContentDigest),
            PlayerMediaAssetReference.ThumbnailSprite(
                ProjectId,
                SpriteImageArtifactId,
                SpriteImageDigest),
            new ContentDigest(new string('f', 64)));
        Assert.AreEqual(PlayerBridgeStatus.Stale, stale.Status);

        ProxyFrameMap mismatchedMap = ProxyFrameMap.Create(
            SourceAssetId,
            ProjectId,
            SourceContentDigest,
            SourcePtsIndexDigest,
            ProxyContentDigest,
            new ContentDigest(new string('e', 64)),
            [Time(-1, 10), Time(0), Time(1, 20), Time(1, 10)],
            Time(3, 20),
            fixture.ProxyPtsIndex.Entries
                .Select(static entry => entry.PresentationTime)
                .ToArray());
        PlayerBridgeReadResult mismatched = PlayerBridgeEvidence.CreateReadResult(
            mismatchedMap,
            fixture.ProxyPtsIndex,
            fixture.PreviewAssets,
            PlayerMediaAssetReference.ProxyVideo(
                ProjectId,
                ProxyArtifactId,
                ProxyContentDigest),
            PlayerMediaAssetReference.ThumbnailSprite(
                ProjectId,
                SpriteImageArtifactId,
                SpriteImageDigest));
        Assert.AreEqual(PlayerBridgeStatus.Mismatched, mismatched.Status);

        var handler = new PlayerBridgeRequestHandler(
            new StubReader(available),
            AllowPlayerAccessAuthorization.Instance);
        BridgeRequest staleSeek = new(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerSeekMethod,
            JobId,
            new ContentDigest(new string('f', 64)),
            0);
        using JsonDocument staleResponse = JsonDocument.Parse(
            await handler.HandleAsync(staleSeek));
        Assert.AreEqual(
            "stale",
            staleResponse.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.AreEqual(snapshot.EvidenceDigest, available.Snapshot!.EvidenceDigest);
    }

    [TestMethod]
    public void Evidence_PreservesNegativeProxyStartAndRejectsNoncontiguousTimeline()
    {
        PlayerEvidenceFixture negativeStart = CreateEvidenceFixture(
        [
            new(0, Time(-1, 20), Time(1001, 30000), keyFrame: true),
            new(1, Time(-499, 30000), Time(1499, 30000), keyFrame: false),
            new(2, Time(1, 30), Time(1, 30), keyFrame: true),
        ]);
        PlayerEvidenceFixture gap = CreateEvidenceFixture(
        [
            new(0, Time(0), Time(1, 100), keyFrame: true),
            new(1, Time(1001, 30000), Time(1499, 30000), keyFrame: false),
            new(2, Time(1, 12), Time(1, 30), keyFrame: true),
        ]);

        Assert.AreEqual(
            PlayerBridgeStatus.Available,
            CreateReadResult(negativeStart).Status);
        Assert.AreEqual(
            PlayerBridgeStatus.Mismatched,
            CreateReadResult(gap).Status);
    }

    [TestMethod]
    public void PlayerRequests_RejectUnknownFieldsInvalidNumbersAndUntrustedOrigins()
    {
        string digest = new string('d', 64);
        string validRead = $$$"""
            {"version":1,"requestId":"{{{RequestId}}}","method":"player.read","params":{"jobId":"{{{JobId}}}"}}
            """;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            validRead,
            out BridgeRequest? read,
            out BridgeRequestRejection readRejection));
        Assert.AreEqual(BridgeRequestRejection.None, readRejection);
        Assert.AreEqual(JobId, read!.JobId);

        string[] schemaViolations =
        [
            validRead.Replace("}", ",\"extra\":true}", StringComparison.Ordinal),
            CreateSeekRequest(digest, 0).Replace(
                "\"proxyFrameSequence\":0",
                "\"proxyFrameSequence\":-1",
                StringComparison.Ordinal),
            CreateSeekRequest(digest, 0).Replace(
                "\"proxyFrameSequence\":0",
                "\"proxyFrameSequence\":0.5",
                StringComparison.Ordinal),
            CreateSeekRequest(digest, 0).Replace(
                digest,
                digest.ToUpperInvariant(),
                StringComparison.Ordinal),
            validRead.Replace(
                JobId.ToString(),
                JobId.ToString().ToUpperInvariant(),
                StringComparison.Ordinal),
        ];
        foreach (string violation in schemaViolations)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                violation,
                out _,
                out BridgeRequestRejection rejection), violation);
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            "https://evil.example/index.html",
            validRead,
            out _,
            out BridgeRequestRejection originRejection));
        Assert.AreEqual(
            BridgeRequestRejection.OriginNotAllowed,
            originRejection);

        string unknown = validRead.Replace(
            "player.read",
            "player.delete",
            StringComparison.Ordinal);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            unknown,
            out _,
            out BridgeRequestRejection methodRejection));
        Assert.AreEqual(
            BridgeRequestRejection.MethodNotAllowed,
            methodRejection);
    }

    [TestMethod]
    public void MediaReferences_AllowOnlyOpaqueVirtualOriginAssets()
    {
        string proxy = PlayerMediaAssetReference.ProxyVideo(
            ProjectId,
            ProxyArtifactId,
            ProxyContentDigest).Value;
        string sprite = PlayerMediaAssetReference
            .ThumbnailSprite(
                ProjectId,
                SpriteImageArtifactId,
                SpriteImageDigest)
            .Value;
        Assert.IsTrue(PlayerMediaAssetReference.IsAllowedValue(proxy));
        Assert.IsTrue(PlayerMediaAssetReference.IsAllowedValue(sprite));

        string[] rejected =
        [
            @"C:\private\proxy.mp4",
            "file:///C:/private/proxy.mp4",
            "https://example.com/media/proxy/44444444-4444-4444-8444-444444444444",
            "https://appassets.example/media/proxy/44444444-4444-4444-8444-444444444444?path=C%3A%5Cprivate",
            "https://appassets.example/media/other/44444444-4444-4444-8444-444444444444",
            "https://appassets.example/media/proxy/00000000-0000-0000-0000-000000000000",
        ];
        foreach (string value in rejected)
        {
            Assert.IsFalse(PlayerMediaAssetReference.IsAllowedValue(value), value);
            Assert.IsFalse(WebViewHost.IsAllowedMediaAssetUri(value), value);
        }
    }

    [TestMethod]
    public async Task ReaderFailure_ReturnsStableUnavailableResponseWithoutPrivateDetails()
    {
        var handler = new PlayerBridgeRequestHandler(
            new ThrowingReader(),
            AllowPlayerAccessAuthorization.Instance);
        var request = new BridgeRequest(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerReadMethod,
            JobId);

        string response = await handler.HandleAsync(request);

        Assert.IsFalse(response.Contains(@"C:\private", StringComparison.OrdinalIgnoreCase));
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual(
            "unavailable",
            document.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task AuthorizationPrecedesReaderAndReturnsClosedForbiddenStatus()
    {
        var reader = new CountingReader();
        var handler = new PlayerBridgeRequestHandler(
            reader,
            DeniedPlayerAccessAuthorization.Instance);
        var read = new BridgeRequest(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerReadMethod,
            JobId);
        var seek = new BridgeRequest(
            Guid.Parse(RequestId),
            BridgeProtocol.PlayerSeekMethod,
            JobId,
            new ContentDigest(new string('e', 64)),
            proxyFrameSequence: 0);

        using JsonDocument readResponse = JsonDocument.Parse(
            await handler.HandleAsync(read));
        using JsonDocument seekResponse = JsonDocument.Parse(
            await handler.HandleAsync(seek));

        Assert.AreEqual(0, reader.CallCount);
        Assert.AreEqual(
            "forbidden",
            readResponse.RootElement
                .GetProperty("result")
                .GetProperty("status")
                .GetString());
        AssertExactKeys(
            readResponse.RootElement.GetProperty("result"),
            "status",
            "snapshot");
        Assert.AreEqual(
            JsonValueKind.Null,
            readResponse.RootElement
                .GetProperty("result")
                .GetProperty("snapshot")
                .ValueKind);
        Assert.AreEqual(
            "forbidden",
            seekResponse.RootElement
                .GetProperty("result")
                .GetProperty("status")
                .GetString());
        AssertExactKeys(
            seekResponse.RootElement.GetProperty("result"),
            "status",
            "position");
        Assert.AreEqual(
            JsonValueKind.Null,
            seekResponse.RootElement
                .GetProperty("result")
                .GetProperty("position")
                .ValueKind);
    }

    [TestMethod]
    public void PlayerAccessAuthorizationUsesCurrentNativeActorPreviewPermission()
    {
        using var fixture = new PlayerAuthorizationFixture();
        string currentActorSid = OtherActorSid;
        var authorization = new PlayerAccessAuthorization(
            fixture.AuthorizationService,
            () => currentActorSid);

        AuthorizationException denied =
            Assert.ThrowsExactly<AuthorizationException>(
                authorization.DemandPreview);
        Assert.AreEqual(
            AuthorizationOperation.PreviewProject,
            denied.Operation);

        Assert.IsTrue(
            fixture.AuthorizationService.BootstrapInitialRoles(ActorSid));
        fixture.AuthorizationService.AssignRole(
            OtherActorSid,
            AuthorizationRole.Viewer,
            ActorSid,
            "Allow preview verification.");
        authorization.DemandPreview();

        fixture.AuthorizationService.RevokeRole(
            OtherActorSid,
            AuthorizationRole.Viewer,
            ActorSid);
        AuthorizationException revoked =
            Assert.ThrowsExactly<AuthorizationException>(
                authorization.DemandPreview);
        Assert.AreEqual(
            AuthorizationOperation.PreviewProject,
            revoked.Operation);
    }

    private static PlayerBridgeReadResult CreateAvailableResult()
    {
        PlayerEvidenceFixture fixture = CreateEvidenceFixture();
        PlayerBridgeReadResult result = CreateReadResult(fixture);
        Assert.AreEqual(PlayerBridgeStatus.Available, result.Status);
        return result;
    }

    private static PlayerBridgeReadResult CreateReadResult(
        PlayerEvidenceFixture fixture) =>
        PlayerBridgeEvidence.CreateReadResult(
            fixture.FrameMap,
            fixture.ProxyPtsIndex,
            fixture.PreviewAssets,
            PlayerMediaAssetReference.ProxyVideo(
                ProjectId,
                ProxyArtifactId,
                ProxyContentDigest),
            PlayerMediaAssetReference.ThumbnailSprite(
                ProjectId,
                SpriteImageArtifactId,
                SpriteImageDigest));

    private static PlayerEvidenceFixture CreateEvidenceFixture(
        IReadOnlyList<PtsIndexEntry>? entries = null,
        int waveformChannelCount = 1)
    {
        PtsIndexEntry[] proxyEntries = entries?.ToArray() ??
        [
            new(0, Time(0), Time(1001, 30000), keyFrame: true),
            new(1, Time(1001, 30000), Time(1499, 30000), keyFrame: false),
            new(2, Time(2500, 30000), Time(1000, 30000), keyFrame: true),
        ];
        var proxyPtsIndex = new PtsIndex(
            SourceAssetId,
            ProjectId,
            JobId,
            "opaque-proxy.mp4",
            ProxyContentDigest,
            byteSize: 1024,
            streamIndex: 0,
            Time(1, 30000),
            proxyEntries);
        ContentDigest proxyPtsIndexDigest = ContentDigest.Compute(
            Encoding.UTF8.GetBytes(proxyPtsIndex.CanonicalJson));
        ProxyFrameMap frameMap = ProxyFrameMap.Create(
            SourceAssetId,
            ProjectId,
            SourceContentDigest,
            SourcePtsIndexDigest,
            ProxyContentDigest,
            proxyPtsIndexDigest,
            [Time(-1, 10), Time(0), Time(1, 20), Time(1, 10)],
            Time(3, 20),
            proxyEntries.Select(static entry => entry.PresentationTime).ToArray());

        WaveformFrame[] waveformFrames = proxyEntries.Select(entry =>
            MediaCheckpointFactory.RestoreWaveformFrame(
                entry.Sequence,
                entry.PresentationTime,
                entry.Duration,
                Enumerable.Range(0, waveformChannelCount)
                    .Select(channelIndex =>
                        MediaCheckpointFactory.RestoreWaveformChannel(
                            channelIndex,
                            checked((short)(-100 - entry.Sequence)),
                            checked((short)(100 + entry.Sequence))))
                    .ToArray()))
            .ToArray();
        PreviewWaveformIndex waveform =
            MediaCheckpointFactory.RestoreWaveformIndex(
                SourceAssetId,
                ProjectId,
                ProxyArtifactId,
                ProxyContentDigest,
                proxyPtsIndexDigest,
                "proxy-preview-assets-v1",
                sampleRate: 8000,
                channelCount: waveformChannelCount,
                channelLayout: waveformChannelCount == 1
                    ? "mono"
                    : "test-multichannel",
                waveformFrames);
        ThumbnailSpriteIndex sprite = MediaCheckpointFactory.RestoreSpriteIndex(
            SourceAssetId,
            ProjectId,
            ProxyArtifactId,
            ProxyContentDigest,
            proxyPtsIndexDigest,
            SpriteImageArtifactId,
            SpriteImageDigest,
            "proxy-preview-assets-v1",
            cellWidth: 160,
            cellHeight: 90,
            columns: 2,
            rows: 1,
            [
                MediaCheckpointFactory.RestoreSpriteCell(
                    0,
                    0,
                    proxyEntries[0].PresentationTime,
                    proxyEntries[0].Duration,
                    0,
                    0),
                MediaCheckpointFactory.RestoreSpriteCell(
                    1,
                    2,
                    proxyEntries[2].PresentationTime,
                    proxyEntries[2].Duration,
                    1,
                    0),
            ]);
        byte[] waveformBytes = Encoding.UTF8.GetBytes(waveform.CanonicalJson);
        byte[] spriteBytes = Encoding.UTF8.GetBytes(sprite.CanonicalJson);
        DateTimeOffset createdAt = new(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var waveformArtifact = new StoredArtifact(
            WaveformArtifactId,
            ProjectId,
            ArtifactKind.Preview,
            $"projects/{ProjectId}/previews/waveform.json",
            ContentDigest.Compute(waveformBytes),
            waveformBytes.LongLength,
            createdAt);
        var spriteImageArtifact = new StoredArtifact(
            SpriteImageArtifactId,
            ProjectId,
            ArtifactKind.Preview,
            $"projects/{ProjectId}/previews/sprite.png",
            SpriteImageDigest,
            byteSize: 1234,
            createdAt);
        var spriteIndexArtifact = new StoredArtifact(
            SpriteIndexArtifactId,
            ProjectId,
            ArtifactKind.Preview,
            $"projects/{ProjectId}/previews/sprite.json",
            ContentDigest.Compute(spriteBytes),
            spriteBytes.LongLength,
            createdAt);
        GeneratedPreviewAssets preview =
            MediaCheckpointFactory.RestoreGeneratedPreviewAssets(
                waveformArtifact,
                spriteImageArtifact,
                spriteIndexArtifact,
                waveform,
                sprite,
                MediaCheckpointFactory.RestorePreviewProfile(
                    "proxy-preview-assets-v1"));
        return new PlayerEvidenceFixture(frameMap, proxyPtsIndex, preview);
    }

    private static string CreateSeekRequest(
        string evidenceDigest,
        int proxyFrameSequence) =>
        $$$"""
        {"version":1,"requestId":"{{{RequestId}}}","method":"player.seek","params":{"jobId":"{{{JobId}}}","digest":"{{{evidenceDigest}}}","proxyFrameSequence":{{{proxyFrameSequence}}}}}
        """;

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static void AssertRational(
        JsonElement element,
        RationalTime expected)
    {
        AssertExactKeys(element, "value", "rate");
        Assert.AreEqual(
            expected.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            element.GetProperty("value").GetString());
        Assert.AreEqual(
            expected.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            element.GetProperty("rate").GetString());
    }

    private static void AssertExactKeys(
        JsonElement element,
        params string[] expected)
    {
        CollectionAssert.AreEquivalent(
            expected,
            element.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
    }

    private sealed record PlayerEvidenceFixture(
        ProxyFrameMap FrameMap,
        PtsIndex ProxyPtsIndex,
        GeneratedPreviewAssets PreviewAssets);

    private sealed class StubReader(PlayerBridgeReadResult result) :
        IPlayerBridgeReader
    {
        public Task<PlayerBridgeReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(JobId, jobId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class CountingReader : IPlayerBridgeReader
    {
        internal int CallCount { get; private set; }

        public Task<PlayerBridgeReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(PlayerBridgeReadResult.Unavailable());
        }
    }

    private sealed class AllowPlayerAccessAuthorization :
        IPlayerAccessAuthorization
    {
        internal static AllowPlayerAccessAuthorization Instance { get; } = new();

        public void DemandPreview()
        {
        }
    }

    private sealed class ThrowingReader : IPlayerBridgeReader
    {
        public Task<PlayerBridgeReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(JobId, jobId);
            throw new IOException(@"C:\private\customer-proxy.mp4");
        }
    }

    private sealed class PlayerAuthorizationFixture : IDisposable
    {
        internal PlayerAuthorizationFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.PlayerAuthorizationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            string databasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(RootPath, "backups"));
            AuthorizationService = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
        }

        private string RootPath { get; }

        internal AuthorizationService AuthorizationService { get; }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}