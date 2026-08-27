using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class SourceSpanTests
{
    private const string SourceDigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void MarkerTagsArePublicAndSealed()
    {
        Type[] markerTypes =
        [
            typeof(SourceSpanIdTag),
            typeof(AssetIdTag),
            typeof(TranscriptRevisionIdTag),
            typeof(TranscriptTokenIdTag),
            typeof(EditorialRecordIdTag),
        ];

        foreach (Type markerType in markerTypes)
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }
    }

    [TestMethod]
    public void ConstructorAllowsNegativeStartAndStoresMediaProvenance()
    {
        EntityId<SourceSpanIdTag> id = EntityId<SourceSpanIdTag>.New();
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        TimeRange range = new(new RationalTime(-90, 30), new RationalTime(150, 30));
        ContentDigest digest = new(SourceDigestHex);

        SourceSpan span = new(
            id,
            assetId,
            "video:0",
            range,
            digest,
            transcriptRevisionId: null,
            transcriptTokenIds: []);

        Assert.AreEqual(id, span.Id);
        Assert.AreEqual(assetId, span.AssetId);
        Assert.AreEqual("video:0", span.StreamId);
        Assert.AreEqual(new RationalTime(-3, 1), span.Range.Start);
        Assert.AreEqual(new RationalTime(5, 1), span.Range.Duration);
        Assert.AreEqual(digest, span.SourceDigest);
        Assert.IsNull(span.TranscriptRevisionId);
        Assert.AreEqual(0, span.TranscriptTokenIds.Count);
    }

    [TestMethod]
    public void ConstructorAllowsWholeUtteranceRevisionWithoutTokenIds()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();

        SourceSpan span = CreateSpan(revisionId, []);

        Assert.AreEqual(revisionId, span.TranscriptRevisionId);
        Assert.AreEqual(0, span.TranscriptTokenIds.Count);
    }

    [TestMethod]
    public void ConstructorStoresTranscriptRevisionAndTokenIds()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        EntityId<TranscriptTokenIdTag>[] tokenIds =
        [
            EntityId<TranscriptTokenIdTag>.New(),
            EntityId<TranscriptTokenIdTag>.New(),
        ];

        SourceSpan span = CreateSpan(revisionId, tokenIds);

        Assert.AreEqual(revisionId, span.TranscriptRevisionId);
        CollectionAssert.AreEqual(tokenIds, span.TranscriptTokenIds.ToArray());
    }

    [TestMethod]
    public void ConstructorDefensivelyCopiesTranscriptTokenIds()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        EntityId<TranscriptTokenIdTag> originalTokenId =
            EntityId<TranscriptTokenIdTag>.New();
        EntityId<TranscriptTokenIdTag> replacementTokenId =
            EntityId<TranscriptTokenIdTag>.New();
        List<EntityId<TranscriptTokenIdTag>> input = [originalTokenId];

        SourceSpan span = CreateSpan(revisionId, input);
        input[0] = replacementTokenId;
        input.Add(EntityId<TranscriptTokenIdTag>.New());

        Assert.AreEqual(1, span.TranscriptTokenIds.Count);
        Assert.AreEqual(originalTokenId, span.TranscriptTokenIds[0]);

        IList<EntityId<TranscriptTokenIdTag>> exposed =
            (IList<EntityId<TranscriptTokenIdTag>>)span.TranscriptTokenIds;
        Assert.ThrowsExactly<NotSupportedException>(() => exposed.Add(replacementTokenId));
        Assert.ThrowsExactly<NotSupportedException>(() => exposed[0] = replacementTokenId);
    }

    [TestMethod]
    public void ConstructorRejectsNullRequiredReferences()
    {
        EntityId<SourceSpanIdTag> id = EntityId<SourceSpanIdTag>.New();
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        TimeRange range = new(default, new RationalTime(1, 1));
        ContentDigest digest = new(SourceDigestHex);
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> tokenIds =
            Array.Empty<EntityId<TranscriptTokenIdTag>>();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SourceSpan(null!, assetId, "video:0", range, digest, null, tokenIds));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SourceSpan(id, null!, "video:0", range, digest, null, tokenIds));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SourceSpan(id, assetId, null!, range, digest, null, tokenIds));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SourceSpan(id, assetId, "video:0", range, null!, null, tokenIds));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SourceSpan(id, assetId, "video:0", range, digest, null, null!));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [DataRow(" video:0")]
    [DataRow("video:0 ")]
    [DataRow("\r\nvideo:0")]
    [DataRow("video:0\r\n")]
    public void ConstructorRejectsBlankOrUntrimmedStreamId(string streamId)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                EntityId<AssetIdTag>.New(),
                streamId,
                new TimeRange(default, new RationalTime(1, 1)),
                new ContentDigest(SourceDigestHex),
                transcriptRevisionId: null,
                transcriptTokenIds: []));

        Assert.AreEqual("streamId", exception.ParamName);
    }

    [TestMethod]
    public void ConstructorRejectsZeroDurationAndDefaultRanges()
    {
        TimeRange[] invalidRanges =
        [
            default,
            new TimeRange(new RationalTime(-5, 1), default),
        ];

        foreach (TimeRange range in invalidRanges)
        {
            ArgumentOutOfRangeException exception =
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new SourceSpan(
                        EntityId<SourceSpanIdTag>.New(),
                        EntityId<AssetIdTag>.New(),
                        "audio:0",
                        range,
                        new ContentDigest(SourceDigestHex),
                        transcriptRevisionId: null,
                        transcriptTokenIds: []));

            Assert.AreEqual("range", exception.ParamName);
        }
    }

    [TestMethod]
    public void ConstructorRejectsTokenIdsWithoutRevision()
    {
        EntityId<TranscriptTokenIdTag> tokenId = EntityId<TranscriptTokenIdTag>.New();

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => CreateSpan(transcriptRevisionId: null, [tokenId]));

        Assert.AreEqual("transcriptRevisionId", exception.ParamName);
    }

    [TestMethod]
    public void ConstructorRejectsNullTokenIdElements()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        EntityId<TranscriptTokenIdTag>[] tokenIds = [null!];

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => CreateSpan(revisionId, tokenIds));

        Assert.AreEqual("transcriptTokenIds", exception.ParamName);
    }

    [TestMethod]
    public void ConstructorRejectsDuplicateTokenIdsByValue()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        EntityId<TranscriptTokenIdTag> tokenId = EntityId<TranscriptTokenIdTag>.New();
        EntityId<TranscriptTokenIdTag> equivalentTokenId =
            EntityId<TranscriptTokenIdTag>.From(tokenId.Value);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => CreateSpan(revisionId, [tokenId, equivalentTokenId]));

        Assert.AreEqual("transcriptTokenIds", exception.ParamName);
    }

    private static SourceSpan CreateSpan(
        EntityId<TranscriptRevisionIdTag>? transcriptRevisionId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> transcriptTokenIds)
    {
        return new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            EntityId<AssetIdTag>.New(),
            "audio:0",
            new TimeRange(new RationalTime(-1, 2), new RationalTime(3, 2)),
            new ContentDigest(SourceDigestHex),
            transcriptRevisionId,
            transcriptTokenIds);
    }
}
