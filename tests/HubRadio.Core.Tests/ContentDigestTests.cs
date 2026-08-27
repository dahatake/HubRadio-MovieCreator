using HubRadio.Core.Identity;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ContentDigestTests
{
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string UppercaseEmptySha256 =
        "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    private const string MixedCaseEmptySha256 =
        "e3B0c44298Fc1C149aFbF4c8996fB92427Ae41E4649b934Ca495991b7852B855";

    [TestMethod]
    public void Compute_EmptyContent_ReturnsKnownSha256()
    {
        ContentDigest digest = ContentDigest.Compute(ReadOnlySpan<byte>.Empty);

        Assert.AreEqual(EmptySha256, digest.Hex);
        Assert.AreEqual(EmptySha256, digest.ToString());
    }

    [TestMethod]
    public void FromBytes_ExactlyThirtyTwoBytes_RoundTripsThroughHex()
    {
        byte[] bytes = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        ContentDigest digest = ContentDigest.FromBytes(bytes);

        Assert.AreEqual(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
            digest.Hex);
        CollectionAssert.AreEqual(bytes, Convert.FromHexString(digest.Hex));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(31)]
    [DataRow(33)]
    [DataRow(64)]
    public void FromBytes_NonSha256Length_Throws(int length)
    {
        byte[] bytes = new byte[length];

        Assert.ThrowsExactly<ArgumentException>(() => ContentDigest.FromBytes(bytes));
    }

    [TestMethod]
    [DataRow(UppercaseEmptySha256)]
    [DataRow(MixedCaseEmptySha256)]
    public void ConstructorAndParse_UpperOrMixedCase_NormalizeToLowercase(string input)
    {
        ContentDigest constructed = new(input);
        ContentDigest parsed = ContentDigest.Parse(input);

        Assert.AreEqual(EmptySha256, constructed.Hex);
        Assert.AreEqual(EmptySha256, constructed.ToString());
        Assert.AreEqual(constructed, parsed);
    }

    [TestMethod]
    public void ConstructorAndParse_Null_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ContentDigest(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ContentDigest.Parse(null!));
    }

    [TestMethod]
    public void ConstructorParseAndTryParse_InvalidRepresentations_AreRejected()
    {
        string[] invalidValues =
        [
            string.Empty,
            " ",
            new string(' ', 64),
            $"sha256:{EmptySha256}",
            EmptySha256[..^1],
            EmptySha256 + "0",
            "g" + EmptySha256[1..],
            EmptySha256[..^1] + "g",
            " " + EmptySha256,
            EmptySha256 + " ",
            "\t" + EmptySha256,
            EmptySha256 + "\r\n",
        ];

        foreach (string invalidValue in invalidValues)
        {
            Assert.ThrowsExactly<ArgumentException>(() => new ContentDigest(invalidValue));
            Assert.ThrowsExactly<FormatException>(() => ContentDigest.Parse(invalidValue));
            Assert.IsFalse(ContentDigest.TryParse(invalidValue, out ContentDigest? digest));
            Assert.IsNull(digest);
        }
    }

    [TestMethod]
    public void TryParse_Null_ReturnsFalseAndNull()
    {
        bool success = ContentDigest.TryParse(null, out ContentDigest? digest);

        Assert.IsFalse(success);
        Assert.IsNull(digest);
    }

    [TestMethod]
    public void TryParse_ValidUppercase_ReturnsCanonicalDigest()
    {
        bool success = ContentDigest.TryParse(UppercaseEmptySha256, out ContentDigest? digest);

        Assert.IsTrue(success);
        Assert.IsNotNull(digest);
        Assert.AreEqual(EmptySha256, digest.Hex);
    }

    [TestMethod]
    public void EqualDigests_HaveValueEqualityAndMatchingHashCodes()
    {
        ContentDigest first = new(EmptySha256);
        ContentDigest equivalent = new(UppercaseEmptySha256);
        ContentDigest different = new(new string('0', 64));

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.IsTrue(first == equivalent);
        Assert.IsFalse(first != equivalent);
        Assert.AreNotEqual(first, different);
        Assert.IsTrue(first != different);
        Assert.IsFalse(first.Equals(null));
    }
}