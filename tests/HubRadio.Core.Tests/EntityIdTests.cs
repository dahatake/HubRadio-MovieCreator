using HubRadio.Core.Identity;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class EntityIdTests
{
    private const string CanonicalGuid = "00112233-4455-6677-8899-aabbccddeeff";
    private const string UppercaseGuid = "00112233-4455-6677-8899-AABBCCDDEEFF";
    private const string MixedCaseGuid = "00112233-4455-6677-8899-AaBbCcDdEeFf";
    private const string OtherCanonicalGuid = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string EmptyCanonicalGuid = "00000000-0000-0000-0000-000000000000";

    [TestMethod]
    public void New_ReturnsNonEmptyUniqueIds()
    {
        EntityId<AssetTag> first = EntityId<AssetTag>.New();
        EntityId<AssetTag> second = EntityId<AssetTag>.New();

        Assert.AreNotEqual(Guid.Empty, first.Value);
        Assert.AreNotEqual(Guid.Empty, second.Value);
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    [DataRow(UppercaseGuid)]
    [DataRow(MixedCaseGuid)]
    public void Parse_NonLowercaseDFormat_ReturnsCanonicalLowercaseId(string input)
    {
        EntityId<AssetTag> entityId = EntityId<AssetTag>.Parse(input);

        Assert.AreEqual(Guid.ParseExact(CanonicalGuid, "D"), entityId.Value);
        Assert.AreEqual(CanonicalGuid, entityId.ToString());
    }

    [TestMethod]
    public void From_EmptyGuid_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => EntityId<AssetTag>.From(Guid.Empty));
    }

    [TestMethod]
    public void Parse_Null_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => EntityId<AssetTag>.Parse(null!));
    }

    [TestMethod]
    public void ParseAndTryParse_InvalidRepresentations_AreRejected()
    {
        string[] invalidValues =
        [
            string.Empty,
            " ",
            EmptyCanonicalGuid,
            "00112233445566778899aabbccddeeff",
            "{00112233-4455-6677-8899-aabbccddeeff}",
            "(00112233-4455-6677-8899-aabbccddeeff)",
            " 00112233-4455-6677-8899-aabbccddeeff",
            "00112233-4455-6677-8899-aabbccddeeff ",
            "00112233-4455-6677-8899-aabbccddeefg",
            "00112233-4455-6677-8899aabbccddeeff",
            "urn:uuid:00112233-4455-6677-8899-aabbccddeeff",
        ];

        foreach (string invalidValue in invalidValues)
        {
            Assert.ThrowsExactly<FormatException>(() => EntityId<AssetTag>.Parse(invalidValue));
            Assert.IsFalse(EntityId<AssetTag>.TryParse(invalidValue, out EntityId<AssetTag>? entityId));
            Assert.IsNull(entityId);
        }
    }

    [TestMethod]
    public void TryParse_Null_ReturnsFalseAndNull()
    {
        bool success = EntityId<AssetTag>.TryParse(null, out EntityId<AssetTag>? entityId);

        Assert.IsFalse(success);
        Assert.IsNull(entityId);
    }

    [TestMethod]
    public void TryParse_UppercaseDFormat_ReturnsCanonicalId()
    {
        bool success = EntityId<AssetTag>.TryParse(UppercaseGuid, out EntityId<AssetTag>? entityId);

        Assert.IsTrue(success);
        Assert.IsNotNull(entityId);
        Assert.AreEqual(CanonicalGuid, entityId.ToString());
    }

    [TestMethod]
    public void EqualIds_WorkAsDictionaryKeys()
    {
        EntityId<AssetTag> first = EntityId<AssetTag>.Parse(CanonicalGuid);
        EntityId<AssetTag> equivalent = EntityId<AssetTag>.From(first.Value);
        EntityId<AssetTag> different = EntityId<AssetTag>.Parse(OtherCanonicalGuid);
        Dictionary<EntityId<AssetTag>, string> values = new()
        {
            [first] = "asset",
        };

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.IsTrue(first == equivalent);
        Assert.IsFalse(first != equivalent);
        Assert.AreNotEqual(first, different);
        Assert.IsTrue(first != different);
        Assert.IsTrue(values.TryGetValue(equivalent, out string? storedValue));
        Assert.AreEqual("asset", storedValue);
    }

    [TestMethod]
    public void SameGuidWithDifferentTags_IsNotEqualWhenBoxed()
    {
        Guid value = Guid.ParseExact(CanonicalGuid, "D");
        object assetId = EntityId<AssetTag>.From(value);
        object projectId = EntityId<ProjectTag>.From(value);

        Assert.AreNotEqual(assetId, projectId);
        Assert.IsFalse(assetId.Equals(projectId));
    }

    [TestMethod]
    public void CanonicalString_RoundTrips()
    {
        EntityId<AssetTag> original = EntityId<AssetTag>.New();

        EntityId<AssetTag> reparsed = EntityId<AssetTag>.Parse(original.ToString());

        Assert.AreEqual(original, reparsed);
        Assert.AreEqual(original.Value, reparsed.Value);
        Assert.AreEqual(original.ToString(), reparsed.ToString());
    }

    private sealed class AssetTag
    {
    }

    private sealed class ProjectTag
    {
    }
}