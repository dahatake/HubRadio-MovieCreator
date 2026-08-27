using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class OriginRefTests
{
    [TestMethod]
    public void OriginKindDefinesExactlyTheThreeSupportedVariants()
    {
        OriginKind[] expected =
        [
            OriginKind.SourceSpan,
            OriginKind.Asset,
            OriginKind.EditorialRecord,
        ];

        CollectionAssert.AreEqual(expected, Enum.GetValues<OriginKind>());
        Assert.AreEqual(0, typeof(OriginRef).GetConstructors().Length);
    }

    [TestMethod]
    public void FromSourceSpanCreatesTypedSourceSpanVariant()
    {
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();

        OriginRef origin = OriginRef.FromSourceSpan(sourceSpanId);

        Assert.AreEqual(OriginKind.SourceSpan, origin.Kind);
        Assert.AreEqual(sourceSpanId, origin.SourceSpanId);
        Assert.IsNull(origin.AssetId);
        Assert.IsNull(origin.EditorialRecordId);
    }

    [TestMethod]
    public void FromAssetCreatesTypedAssetReference()
    {
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();

        OriginRef origin = OriginRef.FromAsset(assetId);

        Assert.AreEqual(OriginKind.Asset, origin.Kind);
        Assert.AreEqual(assetId, origin.AssetId);
        Assert.IsNull(origin.SourceSpanId);
        Assert.IsNull(origin.EditorialRecordId);
    }

    [TestMethod]
    public void FromEditorialRecordCreatesTypedEditorialRecordVariant()
    {
        EntityId<EditorialRecordIdTag> editorialRecordId =
            EntityId<EditorialRecordIdTag>.New();

        OriginRef origin = OriginRef.FromEditorialRecord(editorialRecordId);

        Assert.AreEqual(OriginKind.EditorialRecord, origin.Kind);
        Assert.AreEqual(editorialRecordId, origin.EditorialRecordId);
        Assert.IsNull(origin.SourceSpanId);
        Assert.IsNull(origin.AssetId);
    }

    [TestMethod]
    public void EqualVariantsHaveValueEqualityAndMatchingHashCodes()
    {
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        EntityId<EditorialRecordIdTag> editorialRecordId =
            EntityId<EditorialRecordIdTag>.New();
        (OriginRef First, OriginRef Equivalent)[] pairs =
        [
            (
                OriginRef.FromSourceSpan(sourceSpanId),
                OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.From(sourceSpanId.Value))),
            (
                OriginRef.FromAsset(assetId),
                OriginRef.FromAsset(EntityId<AssetIdTag>.From(assetId.Value))),
            (
                OriginRef.FromEditorialRecord(editorialRecordId),
                OriginRef.FromEditorialRecord(
                    EntityId<EditorialRecordIdTag>.From(editorialRecordId.Value))),
        ];

        foreach ((OriginRef first, OriginRef equivalent) in pairs)
        {
            Assert.AreEqual(first, equivalent);
            Assert.IsTrue(first.Equals(equivalent));
            Assert.IsTrue(first == equivalent);
            Assert.IsFalse(first != equivalent);
            Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        }
    }

    [TestMethod]
    public void DifferentIdsOrKindsAreNotEqual()
    {
        Guid sharedValue = Guid.NewGuid();
        OriginRef firstSource = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.From(sharedValue));
        OriginRef differentSource = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        OriginRef assetWithSameGuid = OriginRef.FromAsset(
            EntityId<AssetIdTag>.From(sharedValue));
        OriginRef editorial = OriginRef.FromEditorialRecord(
            EntityId<EditorialRecordIdTag>.New());

        Assert.AreNotEqual(firstSource, differentSource);
        Assert.IsTrue(firstSource != differentSource);
        Assert.AreNotEqual(firstSource, assetWithSameGuid);
        Assert.IsFalse(firstSource.Equals(assetWithSameGuid));
        Assert.AreNotEqual(assetWithSameGuid, editorial);
    }

    [TestMethod]
    public void FactoriesRejectNullTypedIds()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => OriginRef.FromSourceSpan(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => OriginRef.FromAsset(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => OriginRef.FromEditorialRecord(null!));
    }

    [TestMethod]
    public void EqualityOperatorsHandleNulls()
    {
        OriginRef origin = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        OriginRef? missing = null;
        OriginRef? alsoMissing = null;

        Assert.IsFalse(origin.Equals(null));
        Assert.IsFalse(origin == missing);
        Assert.IsTrue(origin != missing);
        Assert.IsFalse(missing == origin);
        Assert.IsTrue(missing != origin);
        Assert.IsTrue(missing == alsoMissing);
        Assert.IsFalse(missing != alsoMissing);
    }
}
