using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RenderProfileTests
{
    private const string ProfileDigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string TemplateDigestHex =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [TestMethod]
    public void MarkerTagIsPublicAndSealed()
    {
        Assert.IsTrue(typeof(RenderProfileIdTag).IsPublic);
        Assert.IsTrue(typeof(RenderProfileIdTag).IsSealed);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ConstructorStoresVersionedOutputSpecificationAndExactFrameRate(bool subtitleBurnIn)
    {
        EntityId<RenderProfileIdTag> id = EntityId<RenderProfileIdTag>.New();
        EntityId<LayoutSpecIdTag> layoutSpecId = EntityId<LayoutSpecIdTag>.New();
        EntityId<TemplateIdTag> templateId = EntityId<TemplateIdTag>.New();
        ContentDigest profileDigest = new(ProfileDigestHex);
        ContentDigest templateDigest = new(TemplateDigestHex);

        RenderProfile profile = new(
            id,
            "vertical-1080x1920",
            profileDigest,
            1080,
            1920,
            60000,
            2002,
            layoutSpecId,
            templateId,
            templateDigest,
            "h264_nvenc",
            "aac",
            subtitleBurnIn);

        Assert.AreEqual(id, profile.Id);
        Assert.AreEqual("vertical-1080x1920", profile.Name);
        Assert.AreEqual(profileDigest, profile.ProfileDigest);
        Assert.AreEqual(1080, profile.Width);
        Assert.AreEqual(1920, profile.Height);
        Assert.AreEqual(new RationalNumber(30000, 1001), profile.FrameRate);
        Assert.AreEqual(30000L, profile.FrameRateNumerator);
        Assert.AreEqual(1001L, profile.FrameRateDenominator);
        Assert.AreEqual(layoutSpecId, profile.LayoutSpecId);
        Assert.AreEqual(templateId, profile.TemplateId);
        Assert.AreEqual(templateDigest, profile.TemplateVersionDigest);
        Assert.AreEqual("h264_nvenc", profile.VideoCodec);
        Assert.AreEqual("aac", profile.AudioCodec);
        Assert.AreEqual(subtitleBurnIn, profile.SubtitleBurnIn);
    }

    [TestMethod]
    public void ConstructorRejectsNonPositivePixelDimensions()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => CreateProfile(width: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => CreateProfile(width: -1));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => CreateProfile(height: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => CreateProfile(height: -1));
    }

    [TestMethod]
    public void ConstructorRejectsNonPositiveFrameRateComponents()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "frameRateNumerator",
            () => CreateProfile(frameRateNumerator: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "frameRateNumerator",
            () => CreateProfile(frameRateNumerator: -1));
        AssertParamName<ArgumentOutOfRangeException>(
            "frameRateDenominator",
            () => CreateProfile(frameRateDenominator: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "frameRateDenominator",
            () => CreateProfile(frameRateDenominator: -1));
    }

    [TestMethod]
    public void ConstructorRejectsNullTypedReferencesAndDigests()
    {
        EntityId<RenderProfileIdTag> id = EntityId<RenderProfileIdTag>.New();
        EntityId<LayoutSpecIdTag> layoutSpecId = EntityId<LayoutSpecIdTag>.New();
        EntityId<TemplateIdTag> templateId = EntityId<TemplateIdTag>.New();
        ContentDigest profileDigest = new(ProfileDigestHex);
        ContentDigest templateDigest = new(TemplateDigestHex);

        RenderProfile Construct(
            EntityId<RenderProfileIdTag> profileId,
            ContentDigest versionDigest,
            EntityId<LayoutSpecIdTag> layoutId,
            EntityId<TemplateIdTag> referencedTemplateId,
            ContentDigest referencedTemplateDigest)
        {
            return new RenderProfile(
                profileId,
                "landscape-1280x720",
                versionDigest,
                1280,
                720,
                30000,
                1001,
                layoutId,
                referencedTemplateId,
                referencedTemplateDigest,
                "h264",
                "aac",
                subtitleBurnIn: true);
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => Construct(null!, profileDigest, layoutSpecId, templateId, templateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => Construct(id, null!, layoutSpecId, templateId, templateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => Construct(id, profileDigest, null!, templateId, templateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => Construct(id, profileDigest, layoutSpecId, null!, templateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => Construct(id, profileDigest, layoutSpecId, templateId, null!));

        RenderProfile valid = Construct(
            id,
            profileDigest,
            layoutSpecId,
            templateId,
            templateDigest);
        Assert.AreEqual(id, valid.Id);
    }

    [TestMethod]
    public void ConstructorRejectsBlankOrUntrimmedNamesAndCodecs()
    {
        string[] invalidText = [string.Empty, " ", "\t", " leading", "trailing ", "\r\n"];

        foreach (string value in invalidText)
        {
            AssertParamName<ArgumentException>("name", () => CreateProfile(name: value));
            AssertParamName<ArgumentException>(
                "videoCodec",
                () => CreateProfile(videoCodec: value));
            AssertParamName<ArgumentException>(
                "audioCodec",
                () => CreateProfile(audioCodec: value));
        }

        Assert.ThrowsExactly<ArgumentNullException>(() => CreateProfile(name: null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CreateProfile(videoCodec: null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CreateProfile(audioCodec: null!));
    }

    [TestMethod]
    public void EquivalentSpecificationsUseValueEquality()
    {
        EntityId<RenderProfileIdTag> id = EntityId<RenderProfileIdTag>.New();
        EntityId<LayoutSpecIdTag> layoutSpecId = EntityId<LayoutSpecIdTag>.New();
        EntityId<TemplateIdTag> templateId = EntityId<TemplateIdTag>.New();
        RenderProfile first = CreateProfile(
            id: id,
            layoutSpecId: layoutSpecId,
            templateId: templateId,
            frameRateNumerator: 30000,
            frameRateDenominator: 1001);
        RenderProfile equivalent = CreateProfile(
            id: EntityId<RenderProfileIdTag>.From(id.Value),
            layoutSpecId: EntityId<LayoutSpecIdTag>.From(layoutSpecId.Value),
            templateId: EntityId<TemplateIdTag>.From(templateId.Value),
            profileDigest: new ContentDigest(ProfileDigestHex.ToUpperInvariant()),
            templateVersionDigest: new ContentDigest(TemplateDigestHex.ToUpperInvariant()),
            frameRateNumerator: 60000,
            frameRateDenominator: 2002);

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.IsFalse(first.Equals(CreateProfile()));
    }

    [TestMethod]
    public void RenderProfileContainsNoExecutionManifestOrResultState()
    {
        string[] propertyNames = typeof(RenderProfile)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        foreach (string prohibited in new[]
        {
            "Status",
            "Result",
            "Manifest",
            "StartedAt",
            "CompletedAt",
            "OutputPath",
            "ExitCode",
        })
        {
            Assert.IsFalse(propertyNames.Contains(prohibited, StringComparer.Ordinal));
        }
    }

    private static RenderProfile CreateProfile(
        EntityId<RenderProfileIdTag>? id = null,
        string name = "landscape-1280x720",
        ContentDigest? profileDigest = null,
        int width = 1280,
        int height = 720,
        long frameRateNumerator = 30000,
        long frameRateDenominator = 1001,
        EntityId<LayoutSpecIdTag>? layoutSpecId = null,
        EntityId<TemplateIdTag>? templateId = null,
        ContentDigest? templateVersionDigest = null,
        string videoCodec = "h264",
        string audioCodec = "aac",
        bool subtitleBurnIn = true)
    {
        return new RenderProfile(
            id ?? EntityId<RenderProfileIdTag>.New(),
            name,
            profileDigest ?? new ContentDigest(ProfileDigestHex),
            width,
            height,
            frameRateNumerator,
            frameRateDenominator,
            layoutSpecId ?? EntityId<LayoutSpecIdTag>.New(),
            templateId ?? EntityId<TemplateIdTag>.New(),
            templateVersionDigest ?? new ContentDigest(TemplateDigestHex),
            videoCodec,
            audioCodec,
            subtitleBurnIn);
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}