using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ArtifactCacheKeyTests
{
    [TestMethod]
    public void SameDomainOperationConfigurationAndOrderedInputsProduceEqualKeys()
    {
        ContentDigest configuration = Digest('a');
        ContentDigest[] inputs = [Digest('1'), Digest('2')];

        ArtifactCacheKey first = ArtifactCacheKey.Create("render.preview", configuration, inputs);
        ArtifactCacheKey second = ArtifactCacheKey.Create(
            "render.preview",
            new ContentDigest(configuration.Hex.ToUpperInvariant()),
            [Digest('1'), Digest('2')]);

        Assert.AreEqual(first, second);
        Assert.IsTrue(first == second);
        Assert.IsFalse(first != second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreEqual(first.Digest.Hex, first.ToString());
    }

    [TestMethod]
    public void OperationConfigurationAndEveryInputDigestAffectTheKey()
    {
        ArtifactCacheKey baseline = ArtifactCacheKey.Create(
            "render.preview",
            Digest('a'),
            [Digest('1'), Digest('2'), Digest('3')]);

        ArtifactCacheKey[] changed =
        [
            ArtifactCacheKey.Create("render.final", Digest('a'), [Digest('1'), Digest('2'), Digest('3')]),
            ArtifactCacheKey.Create("render.preview", Digest('b'), [Digest('1'), Digest('2'), Digest('3')]),
            ArtifactCacheKey.Create("render.preview", Digest('a'), [Digest('4'), Digest('2'), Digest('3')]),
            ArtifactCacheKey.Create("render.preview", Digest('a'), [Digest('1'), Digest('4'), Digest('3')]),
            ArtifactCacheKey.Create("render.preview", Digest('a'), [Digest('1'), Digest('2'), Digest('4')]),
            ArtifactCacheKey.Create("render.preview", Digest('a'), [Digest('1'), Digest('2')]),
            ArtifactCacheKey.Create("render.preview", Digest('a'), [Digest('1'), Digest('2'), Digest('3'), Digest('4')]),
        ];

        Assert.IsTrue(changed.All(key => key != baseline));
        Assert.AreEqual(changed.Length, changed.Select(static key => key.Digest.Hex).Distinct().Count());
    }

    [TestMethod]
    public void InputOrderIsPartOfTheCacheKey()
    {
        ArtifactCacheKey first = ArtifactCacheKey.Create(
            "compose",
            Digest('a'),
            [Digest('1'), Digest('2')]);
        ArtifactCacheKey reordered = ArtifactCacheKey.Create(
            "compose",
            Digest('a'),
            [Digest('2'), Digest('1')]);

        Assert.AreNotEqual(first, reordered);
    }

    [TestMethod]
    public void CanonicalConfigurationIgnoresPropertyOrderButPreservesArrayOrder()
    {
        ContentDigest firstConfiguration = CanonicalJson.ComputeDigest(
            Encoding.UTF8.GetBytes("{\"quality\":2,\"tracks\":[\"v\",\"a\"]}"));
        ContentDigest reorderedProperties = CanonicalJson.ComputeDigest(
            Encoding.UTF8.GetBytes("{\"tracks\":[\"v\",\"a\"],\"quality\":2}"));
        ContentDigest reorderedTracks = CanonicalJson.ComputeDigest(
            Encoding.UTF8.GetBytes("{\"quality\":2,\"tracks\":[\"a\",\"v\"]}"));

        ArtifactCacheKey first = ArtifactCacheKey.Create("render", firstConfiguration, [Digest('1')]);
        ArtifactCacheKey same = ArtifactCacheKey.Create("render", reorderedProperties, [Digest('1')]);
        ArtifactCacheKey changed = ArtifactCacheKey.Create("render", reorderedTracks, [Digest('1')]);

        Assert.AreEqual(first, same);
        Assert.AreNotEqual(first, changed);
    }

    [TestMethod]
    public void DomainIsExplicitAndPreimageFieldsAreUnambiguous()
    {
        string? domain = typeof(ArtifactCacheKey)
            .GetField(nameof(ArtifactCacheKey.Domain))!
            .GetRawConstantValue() as string;
        Assert.AreEqual(
            "HubRadio.MovieCreator.ArtifactCacheKey/v1",
            domain);

        ArtifactCacheKey first = ArtifactCacheKey.Create("ab", Digest('a'), []);
        ArtifactCacheKey second = ArtifactCacheKey.Create("a", Digest('b'), []);

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void InvalidOperationAndInputCollectionFailClosed()
    {
        AssertParamName<ArgumentException>(
            "operation",
            () => ArtifactCacheKey.Create(" ", Digest('a'), []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => ArtifactCacheKey.Create("render", null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => ArtifactCacheKey.Create("render", Digest('a'), null!));
        AssertParamName<ArgumentException>(
            "inputDigests",
            () => ArtifactCacheKey.Create("render", Digest('a'), [Digest('1'), null!]));
    }

    [TestMethod]
    public void DistinctUnpairedSurrogateOperationsAreRejectedInsteadOfCollapsing()
    {
        Assert.ThrowsExactly<EncoderFallbackException>(
            () => ArtifactCacheKey.Create("\uD800", Digest('a'), []));
        Assert.ThrowsExactly<EncoderFallbackException>(
            () => ArtifactCacheKey.Create("\uD801", Digest('a'), []));
    }

    private static ContentDigest Digest(char digit) => new(new string(digit, 64));

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}