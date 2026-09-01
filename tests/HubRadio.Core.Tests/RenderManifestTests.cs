using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RenderManifestTests
{
    [TestMethod]
    public void MarkerTagsAndManifestContractsAreImmutable()
    {
        Assert.IsTrue(typeof(RenderManifestIdTag).IsPublic);
        Assert.IsTrue(typeof(RenderManifestIdTag).IsSealed);
        Assert.IsTrue(typeof(DeliveryManifestIdTag).IsPublic);
        Assert.IsTrue(typeof(DeliveryManifestIdTag).IsSealed);
        Assert.IsTrue(typeof(SubtitleDeliveryVariantKind).IsPublic);
        Assert.IsTrue(typeof(SubtitleDeliveryVariantKind).IsEnum);

        AssertPublicGetOnlyProperties(
            typeof(RenderManifest),
            "Id",
            "ProjectId",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "TimelineMapId",
            "TimelineMapDigest",
            "ProfileId",
            "ProfileDigest",
            "TemplateId",
            "TemplateVersionDigest",
            "ConfigurationDigest",
            "ApplicationVersion",
            "ApplicationDigest",
            "Inputs",
            "Tools",
            "Models",
            "Fonts",
            "Outputs");
        AssertPublicGetOnlyProperties(
            typeof(DeliveryManifest),
            "Id",
            "ProjectId",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "RenderManifestId",
            "ProfileId",
            "ProfileDigest",
            "Artifacts",
            "SubtitleVariant");
        AssertPublicGetOnlyProperties(
            typeof(SubtitleDeliveryVariantEvidence),
            "Kind",
            "RenderGraphId",
            "DisplayProfileDigest",
            "SubtitleEvidenceDigest",
            "SidecarArtifact");
    }

    [TestMethod]
    public void RenderManifestBindsActualVersionProfileTimelineTemplateAndEvidence()
    {
        CompositionVersion version = CreateCompositionVersion();
        TimelineMap map = CreateTimelineMap(version.Id);
        RenderProfile profile = CreateProfile();
        RenderManifest manifest = CreateRenderManifest(version, map, profile);

        Assert.AreEqual(version.ProjectId, manifest.ProjectId);
        Assert.AreEqual(version.Id, manifest.CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, manifest.CompositionVersionDigest);
        Assert.AreEqual(map.Id, manifest.TimelineMapId);
        Assert.AreEqual(Digest('2'), manifest.TimelineMapDigest);
        Assert.AreEqual(profile.Id, manifest.ProfileId);
        Assert.AreEqual(profile.ProfileDigest, manifest.ProfileDigest);
        Assert.AreEqual(profile.TemplateId, manifest.TemplateId);
        Assert.AreEqual(profile.TemplateVersionDigest, manifest.TemplateVersionDigest);
        Assert.AreEqual(Digest('3'), manifest.ConfigurationDigest);
        Assert.AreEqual("1.0.0+commit.abcdef", manifest.ApplicationVersion);
        Assert.AreEqual(Digest('4'), manifest.ApplicationDigest);
        Assert.AreEqual("source-video", manifest.Inputs.Single().Name);
        Assert.AreEqual("ffmpeg", manifest.Tools.Single().Name);
        Assert.AreEqual("local-layout-model", manifest.Models.Single().ModelIdentifier);
        Assert.AreEqual("Segoe UI Bold", manifest.Fonts.Single().Name);
        Assert.AreEqual("video", manifest.Outputs.Single().ArtifactType);
    }

    [TestMethod]
    public void RenderManifestRejectsTimelineMapForAnotherCompositionVersion()
    {
        CompositionVersion version = CreateCompositionVersion();
        TimelineMap wrongMap = CreateTimelineMap(EntityId<CompositionVersionIdTag>.New());

        AssertParamName<ArgumentException>(
            "timelineMap",
            () => CreateRenderManifest(version, wrongMap, CreateProfile()));
    }

    [TestMethod]
    public void RenderManifestDefensivelyCopiesEveryEvidenceCollection()
    {
        CompositionVersion version = CreateCompositionVersion();
        TimelineMap map = CreateTimelineMap(version.Id);
        RenderProfile profile = CreateProfile();
        List<RenderInputEvidence> inputs = [CreateInput()];
        List<RenderToolEvidence> tools = [CreateTool()];
        List<RenderModelEvidence> models = [CreateModel()];
        List<RenderFontEvidence> fonts = [CreateFont()];
        List<ManifestArtifact> outputs = [CreateArtifact()];

        RenderManifest manifest = new(
            EntityId<RenderManifestIdTag>.New(),
            version,
            map,
            Digest('2'),
            profile,
            Digest('3'),
            "1.0.0",
            Digest('4'),
            inputs,
            tools,
            models,
            fonts,
            outputs);

        inputs.Clear();
        tools.Clear();
        models.Clear();
        fonts.Clear();
        outputs.Clear();

        Assert.AreEqual(1, manifest.Inputs.Count);
        Assert.AreEqual(1, manifest.Tools.Count);
        Assert.AreEqual(1, manifest.Models.Count);
        Assert.AreEqual(1, manifest.Fonts.Count);
        Assert.AreEqual(1, manifest.Outputs.Count);
        AssertReadOnly(manifest.Inputs);
        AssertReadOnly(manifest.Tools);
        AssertReadOnly(manifest.Models);
        AssertReadOnly(manifest.Fonts);
        AssertReadOnly(manifest.Outputs);
    }

    [TestMethod]
    public void RequiredEvidenceCollectionsRejectEmptyNullAndExactDuplicates()
    {
        CompositionVersion version = CreateCompositionVersion();
        TimelineMap map = CreateTimelineMap(version.Id);
        RenderProfile profile = CreateProfile();
        RenderInputEvidence input = CreateInput();
        RenderToolEvidence tool = CreateTool();
        ManifestArtifact output = CreateArtifact();

        AssertParamName<ArgumentException>(
            "inputs",
            () => CreateRenderManifest(version, map, profile, inputs: []));
        AssertParamName<ArgumentException>(
            "tools",
            () => CreateRenderManifest(version, map, profile, tools: []));
        AssertParamName<ArgumentException>(
            "outputs",
            () => CreateRenderManifest(version, map, profile, outputs: []));
        AssertParamName<ArgumentException>(
            "inputs",
            () => CreateRenderManifest(version, map, profile, inputs: [input, input]));
        AssertParamName<ArgumentException>(
            "tools",
            () => CreateRenderManifest(version, map, profile, tools: [tool, tool]));
        AssertParamName<ArgumentException>(
            "outputs",
            () => CreateRenderManifest(version, map, profile, outputs: [output, output]));
        AssertParamName<ArgumentException>(
            "models",
            () => CreateRenderManifest(version, map, profile, models: [null!]));
        AssertParamName<ArgumentException>(
            "fonts",
            () => CreateRenderManifest(version, map, profile, fonts: [null!]));
    }

    [TestMethod]
    public void OptionalModelAndFontCollectionsMayBeEmpty()
    {
        CompositionVersion version = CreateCompositionVersion();
        RenderManifest manifest = CreateRenderManifest(
            version,
            CreateTimelineMap(version.Id),
            CreateProfile(),
            models: [],
            fonts: []);

        Assert.AreEqual(0, manifest.Models.Count);
        Assert.AreEqual(0, manifest.Fonts.Count);
    }

    [TestMethod]
    public void EvidenceValuesUseOrdinalEqualityAndValidateRequiredText()
    {
        RenderInputEvidence input = CreateInput();
        RenderInputEvidence equivalentInput = new("source-video", Digest('5'));
        RenderToolEvidence tool = CreateTool();
        RenderToolEvidence equivalentTool = CreateTool();
        RenderModelEvidence model = CreateModel();
        RenderModelEvidence equivalentModel = CreateModel();
        RenderFontEvidence font = CreateFont();
        RenderFontEvidence equivalentFont = CreateFont();

        Assert.AreEqual(input, equivalentInput);
        Assert.AreEqual(input.GetHashCode(), equivalentInput.GetHashCode());
        Assert.AreEqual(tool, equivalentTool);
        Assert.AreEqual(tool.GetHashCode(), equivalentTool.GetHashCode());
        Assert.AreEqual(model, equivalentModel);
        Assert.AreEqual(model.GetHashCode(), equivalentModel.GetHashCode());
        Assert.AreEqual(font, equivalentFont);
        Assert.AreEqual(font.GetHashCode(), equivalentFont.GetHashCode());
        Assert.AreNotEqual(input, new RenderInputEvidence("Source-Video", Digest('5')));

        AssertParamName<ArgumentException>(
            "name",
            () => new RenderInputEvidence(" ", Digest('5')));
        AssertParamName<ArgumentException>(
            "version",
            () => new RenderToolEvidence("ffmpeg", " trailing ", "--enable-gpl", Digest('6'), null));
        AssertParamName<ArgumentException>(
            "modelIdentifier",
            () => new RenderModelEvidence("local", "", "1", Digest('7')));
        AssertParamName<ArgumentException>(
            "name",
            () => new RenderFontEvidence(" leading", Digest('8')));
    }

    [TestMethod]
    public void ArtifactCapturesRequiredEvidenceWithoutOwningPathPolicy()
    {
        ManifestArtifact artifact = CreateArtifact();

        Assert.AreEqual("video", artifact.ArtifactType);
        Assert.AreEqual("video/mp4", artifact.MediaType);
        Assert.AreEqual("ja", artifact.Language);
        Assert.AreEqual(1234L, artifact.ByteSize);
        Assert.AreEqual(Digest('9'), artifact.Sha256);
        Assert.AreEqual("objects/99/render.mp4", artifact.StorageReference);
        Assert.AreEqual(artifact, CreateArtifact());
        Assert.AreEqual(artifact.GetHashCode(), CreateArtifact().GetHashCode());

        ManifestArtifact languageNeutral = new(
            "thumbnail",
            "image/png",
            null,
            42,
            Digest('a'),
            "objects/aa/thumb.png");
        Assert.IsNull(languageNeutral.Language);

        AssertParamName<ArgumentOutOfRangeException>(
            "byteSize",
            () => new ManifestArtifact("video", "video/mp4", null, 0, Digest('9'), "object"));
        AssertParamName<ArgumentException>(
            "language",
            () => new ManifestArtifact("subtitle", "text/vtt", " ", 1, Digest('9'), "object"));
        AssertParamName<ArgumentException>(
            "storageReference",
            () => new ManifestArtifact("video", "video/mp4", null, 1, Digest('9'), " object "));
    }

    [TestMethod]
    public void DeliveryManifestDerivesOneBindingAndProtectsArtifactInventory()
    {
        RenderManifest render = CreateRenderManifest();
        ManifestArtifact video = CreateArtifact();
        ManifestArtifact subtitle = new(
            "subtitle",
            "text/vtt",
            "ja",
            88,
            Digest('a'),
            "objects/aa/subtitle.vtt");
        List<ManifestArtifact> artifacts = [video, subtitle];

        DeliveryManifest delivery = new(
            EntityId<DeliveryManifestIdTag>.New(),
            render,
            artifacts);
        artifacts.Clear();

        Assert.AreEqual(render.ProjectId, delivery.ProjectId);
        Assert.AreEqual(render.CompositionVersionId, delivery.CompositionVersionId);
        Assert.AreEqual(render.CompositionVersionDigest, delivery.CompositionVersionDigest);
        Assert.AreEqual(render.Id, delivery.RenderManifestId);
        Assert.AreEqual(render.ProfileId, delivery.ProfileId);
        Assert.AreEqual(render.ProfileDigest, delivery.ProfileDigest);
        CollectionAssert.AreEqual(new[] { video, subtitle }, delivery.Artifacts.ToArray());
        Assert.IsNull(delivery.SubtitleVariant);
        AssertReadOnly(delivery.Artifacts);
    }

    [TestMethod]
    public void DeliveryManifestExplicitlyBindsSubtitleVariantAndOptionalSidecar()
    {
        RenderManifest render = CreateRenderManifest();
        ManifestArtifact video = CreateArtifact();
        ManifestArtifact sidecar = new(
            "subtitle",
            "text/vtt",
            "ja",
            88,
            Digest('a'),
            "objects/aa/subtitle.vtt");
        var burnedIn = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.BurnedIn,
            EntityId<RenderGraphIdTag>.New(),
            Digest('d'),
            Digest('e'),
            sidecarArtifact: null);
        var subtitleFree = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.SubtitleFree,
            EntityId<RenderGraphIdTag>.New(),
            Digest('d'),
            Digest('e'),
            sidecar);

        DeliveryManifest burnedDelivery = new(
            EntityId<DeliveryManifestIdTag>.New(),
            render,
            [video],
            burnedIn);
        DeliveryManifest freeDelivery = new(
            EntityId<DeliveryManifestIdTag>.New(),
            render,
            [video, sidecar],
            subtitleFree);

        Assert.AreSame(burnedIn, burnedDelivery.SubtitleVariant);
        Assert.AreSame(subtitleFree, freeDelivery.SubtitleVariant);
        Assert.AreSame(sidecar, freeDelivery.SubtitleVariant!.SidecarArtifact);
    }

    [TestMethod]
    public void SubtitleVariantEvidenceRejectsAmbiguousOrUnlocalizedSidecars()
    {
        ManifestArtifact sidecar = new(
            "subtitle",
            "text/vtt",
            "ja",
            88,
            Digest('a'),
            "objects/aa/subtitle.vtt");
        ManifestArtifact unlocalized = new(
            "subtitle",
            "text/vtt",
            language: null,
            88,
            Digest('a'),
            "objects/aa/subtitle.vtt");
        ManifestArtifact wrongType = new(
            "transcript",
            "text/plain",
            "ja",
            88,
            Digest('a'),
            "objects/aa/transcript.txt");

        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => new SubtitleDeliveryVariantEvidence(
                (SubtitleDeliveryVariantKind)99,
                EntityId<RenderGraphIdTag>.New(),
                Digest('d'),
                Digest('e'),
                sidecarArtifact: null));
        AssertParamName<ArgumentException>(
            "sidecarArtifact",
            () => new SubtitleDeliveryVariantEvidence(
                SubtitleDeliveryVariantKind.BurnedIn,
                EntityId<RenderGraphIdTag>.New(),
                Digest('d'),
                Digest('e'),
                sidecar));
        AssertParamName<ArgumentException>(
            "sidecarArtifact",
            () => new SubtitleDeliveryVariantEvidence(
                SubtitleDeliveryVariantKind.SubtitleFree,
                EntityId<RenderGraphIdTag>.New(),
                Digest('d'),
                Digest('e'),
                unlocalized));
        AssertParamName<ArgumentException>(
            "sidecarArtifact",
            () => new SubtitleDeliveryVariantEvidence(
                SubtitleDeliveryVariantKind.SubtitleFree,
                EntityId<RenderGraphIdTag>.New(),
                Digest('d'),
                Digest('e'),
                wrongType));
    }

    [TestMethod]
    public void DeliveryManifestRejectsSubtitleInventoryThatDiffersFromVariantEvidence()
    {
        RenderManifest render = CreateRenderManifest();
        ManifestArtifact video = CreateArtifact();
        ManifestArtifact sidecar = new(
            "subtitle",
            "text/vtt",
            "ja",
            88,
            Digest('a'),
            "objects/aa/subtitle.vtt");
        var subtitleFree = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.SubtitleFree,
            EntityId<RenderGraphIdTag>.New(),
            Digest('d'),
            Digest('e'),
            sidecar);
        var burnedIn = new SubtitleDeliveryVariantEvidence(
            SubtitleDeliveryVariantKind.BurnedIn,
            EntityId<RenderGraphIdTag>.New(),
            Digest('d'),
            Digest('e'),
            sidecarArtifact: null);

        AssertParamName<ArgumentException>(
            "subtitleVariant",
            () => new DeliveryManifest(
                EntityId<DeliveryManifestIdTag>.New(),
                render,
                [video],
                subtitleFree));
        AssertParamName<ArgumentException>(
            "subtitleVariant",
            () => new DeliveryManifest(
                EntityId<DeliveryManifestIdTag>.New(),
                render,
                [video, sidecar],
                burnedIn));
    }

    [TestMethod]
    public void DeliveryManifestRejectsEmptyNullAndDuplicateArtifactInventory()
    {
        RenderManifest render = CreateRenderManifest();
        ManifestArtifact artifact = CreateArtifact();

        AssertParamName<ArgumentException>(
            "artifacts",
            () => new DeliveryManifest(EntityId<DeliveryManifestIdTag>.New(), render, []));
        AssertParamName<ArgumentException>(
            "artifacts",
            () => new DeliveryManifest(
                EntityId<DeliveryManifestIdTag>.New(),
                render,
                [null!]));
        AssertParamName<ArgumentException>(
            "artifacts",
            () => new DeliveryManifest(
                EntityId<DeliveryManifestIdTag>.New(),
                render,
                [artifact, CreateArtifact()]));
    }

    [TestMethod]
    public void ManifestContractsDoNotOwnApprovalJobOrPathPolicyState()
    {
        string[] prohibited =
        [
            "Approval",
            "ApprovalState",
            "Job",
            "JobState",
            "Status",
            "OutputPath",
            "IsPathAllowed",
        ];

        foreach (Type type in new[] { typeof(RenderManifest), typeof(DeliveryManifest) })
        {
            string[] properties = type.GetProperties().Select(static property => property.Name).ToArray();
            foreach (string name in prohibited)
            {
                Assert.IsFalse(properties.Contains(name, StringComparer.Ordinal));
            }
        }
    }

    private static RenderManifest CreateRenderManifest(
        CompositionVersion? version = null,
        TimelineMap? map = null,
        RenderProfile? profile = null,
        IReadOnlyList<RenderInputEvidence>? inputs = null,
        IReadOnlyList<RenderToolEvidence>? tools = null,
        IReadOnlyList<RenderModelEvidence>? models = null,
        IReadOnlyList<RenderFontEvidence>? fonts = null,
        IReadOnlyList<ManifestArtifact>? outputs = null)
    {
        version ??= CreateCompositionVersion();
        map ??= CreateTimelineMap(version.Id);
        profile ??= CreateProfile();

        return new RenderManifest(
            EntityId<RenderManifestIdTag>.New(),
            version,
            map,
            Digest('2'),
            profile,
            Digest('3'),
            "1.0.0+commit.abcdef",
            Digest('4'),
            inputs ?? [CreateInput()],
            tools ?? [CreateTool()],
            models ?? [CreateModel()],
            fonts ?? [CreateFont()],
            outputs ?? [CreateArtifact()]);
    }

    private static CompositionVersion CreateCompositionVersion()
    {
        return new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            createdByActorId: "editor-1",
            createdAt: new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            changeReason: "Prepare render",
            contentDigest: Digest('1'),
            elements: []);
    }

    private static TimelineMap CreateTimelineMap(EntityId<CompositionVersionIdTag> versionId)
    {
        return new TimelineMap(EntityId<TimelineMapIdTag>.New(), versionId, []);
    }

    private static RenderProfile CreateProfile()
    {
        return new RenderProfile(
            EntityId<RenderProfileIdTag>.New(),
            "landscape-1080p",
            Digest('b'),
            1920,
            1080,
            30000,
            1001,
            EntityId<LayoutSpecIdTag>.New(),
            EntityId<TemplateIdTag>.New(),
            Digest('c'),
            "h264",
            "aac",
            subtitleBurnIn: true);
    }

    private static RenderInputEvidence CreateInput() => new("source-video", Digest('5'));

    private static RenderToolEvidence CreateTool() => new(
        "ffmpeg",
        "8.0",
        "--enable-gpl --enable-libx264",
        Digest('6'),
        containerImageDigest: null);

    private static RenderModelEvidence CreateModel() => new(
        "local",
        "local-layout-model",
        "1.0",
        Digest('7'));

    private static RenderFontEvidence CreateFont() => new("Segoe UI Bold", Digest('8'));

    private static ManifestArtifact CreateArtifact() => new(
        "video",
        "video/mp4",
        "ja",
        1234,
        Digest('9'),
        "objects/99/render.mp4");

    private static ContentDigest Digest(char digit) => new(new string(digit, 64));

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<T>)values).Clear());
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        System.Reflection.PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}