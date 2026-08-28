using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class CanonicalJsonTests
{
    [TestMethod]
    public void ObjectPropertiesAreSortedRecursivelyAndWhitespaceIsRemoved()
    {
        const string input = """
            {
              "z": -0.00e+2,
              "a": [3, {"d": 1.20, "c": "\u0061"}],
              "m": true
            }
            """;

        string canonical = CanonicalJson.Canonicalize(input);

        Assert.AreEqual("{\"a\":[3,{\"c\":\"a\",\"d\":12e-1}],\"m\":true,\"z\":0}", canonical);
    }

    [TestMethod]
    public void PropertyOrderAndEquivalentNumberSpellingsProduceIdenticalBytesAndDigest()
    {
        byte[] first = Encoding.UTF8.GetBytes("{\"b\":10e-1,\"a\":1.0}");
        byte[] second = Encoding.UTF8.GetBytes("{ \"a\" : 1, \"b\" : 1.00 }");

        byte[] firstCanonical = CanonicalJson.CanonicalizeToUtf8(first);
        byte[] secondCanonical = CanonicalJson.CanonicalizeToUtf8(second);

        CollectionAssert.AreEqual(firstCanonical, secondCanonical);
        Assert.AreEqual("{\"a\":1,\"b\":1}", Encoding.UTF8.GetString(firstCanonical));
        Assert.AreEqual(
            CanonicalJson.ComputeDigest(first),
            CanonicalJson.ComputeDigest(second));
    }

    [TestMethod]
    public void ArraysPreserveSemanticOrder()
    {
        string first = CanonicalJson.Canonicalize("{\"items\":[1,2,3]}");
        string reordered = CanonicalJson.Canonicalize("{\"items\":[3,2,1]}");

        Assert.AreEqual("{\"items\":[1,2,3]}", first);
        Assert.AreEqual("{\"items\":[3,2,1]}", reordered);
        Assert.AreNotEqual(first, reordered);
    }

    [TestMethod]
    public void ArbitraryPrecisionDecimalNumbersCanonicalizeWithoutFloatingPointLoss()
    {
        const string first = "123456789012345678901234567890.000e-40";
        const string equivalent = "12345678901234567890123456789e-39";

        Assert.AreEqual(
            "12345678901234567890123456789e-39",
            CanonicalJson.Canonicalize(first));
        Assert.AreEqual(
            CanonicalJson.Canonicalize(first),
            CanonicalJson.Canonicalize(equivalent));
    }

    [TestMethod]
    [DataRow("{\"a\":1,\"a\":2}")]
    [DataRow("{\"outer\":{\"a\":1,\"\\u0061\":2}}")]
    [DataRow("[{\"a\":1,\"a\":2}]")]
    public void DuplicatePropertyNamesAreRejectedAtEveryDepth(string json)
    {
        JsonException exception = Assert.ThrowsExactly<JsonException>(
            () => CanonicalJson.Canonicalize(json));
        StringAssert.Contains(exception.Message, "Duplicate JSON property name");
    }

    [TestMethod]
    [DataRow("{\"a\":1,}")]
    [DataRow("{/*comment*/\"a\":1}")]
    [DataRow("{\"a\":")]
    public void NonStandardOrMalformedJsonIsRejected(string json)
    {
        Assert.Throws<JsonException>(() => CanonicalJson.Canonicalize(json));
    }

    [TestMethod]
    public void WriteCanonicalizesAnExistingJsonElementWithoutTypeSpecificAdapters()
    {
        using JsonDocument document = JsonDocument.Parse("{\"b\":null,\"a\":false}");

        byte[] canonical = CanonicalJson.Write(document.RootElement);

        Assert.AreEqual("{\"a\":false,\"b\":null}", Encoding.UTF8.GetString(canonical));
    }

    [TestMethod]
    public void NullStringInputIsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CanonicalJson.Canonicalize(null!));
    }

    [TestMethod]
    public void UnpairedUtf16SurrogateIsRejectedInsteadOfReplaced()
    {
        const string malformedJson = "{\"value\":\"\uD800\"}";

        Assert.ThrowsExactly<EncoderFallbackException>(
            () => CanonicalJson.Canonicalize(malformedJson));
    }
}