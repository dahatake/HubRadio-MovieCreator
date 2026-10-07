using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Profiles;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BundledRenderProfileTests
{
    private const string LandscapeEvidence =
        "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/0";
    private const string VerticalEvidence =
        "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/1";

    private static readonly string[] RequiredUnmeasured =
    [
        "brand-assets",
        "cross-gpu-reproducibility",
        "platform-safe-zone-geometry",
        "production-quality-thresholds",
    ];

    private static readonly ProfileCase[] Profiles =
    [
        new ProfileCase(
            "16x9/long-form.json",
            "14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa",
            "d95a6c34-359a-5c6d-8ed8-15ffd94966bc",
            "bf08c776-0f97-5373-a091-c70b60acd569",
            1280,
            720,
            16,
            9,
            SubtitleBurnIn: true,
            LandscapeEvidence,
            [
                "chapter",
                "topic-display",
                "speaker-name",
                "material",
                "call-to-action",
                "end-card-margin",
                "subtitle",
                "graphic",
            ]),
        new ProfileCase(
            "16x9/youtube-landscape.json",
            "dab576ec-a6d0-557d-9a4c-144f392bd568",
            "2817bbb4-c644-5e84-877c-298d364f46db",
            "d26030d1-d2da-5025-acaf-69ce17114d68",
            1280,
            720,
            16,
            9,
            SubtitleBurnIn: true,
            LandscapeEvidence,
            [
                "chapter",
                "topic-display",
                "speaker-name",
                "material",
                "call-to-action",
                "end-card-margin",
                "subtitle",
                "graphic",
            ]),
        new ProfileCase(
            "16x9/landscape-short.json",
            "de6aa4f5-bef9-5c29-98e0-f4e0289783ed",
            "9b8edad0-250e-5833-8adc-d6e9f93ac2a8",
            "3ed1c245-4856-5f81-a3a1-5102fda37494",
            1280,
            720,
            16,
            9,
            SubtitleBurnIn: true,
            LandscapeEvidence,
            [
                "topic-display",
                "speaker-name",
                "call-to-action",
                "end-card-margin",
                "subtitle",
                "graphic",
            ]),
        new ProfileCase(
            "9x16/vertical-short.json",
            "c04c1910-d1da-5752-a35b-cc8b9e4604d3",
            "f18766a0-78c9-5d08-83e1-ee812cccdcb6",
            "2d41bbd4-6891-51f5-b59f-78e01b37ff02",
            1080,
            1920,
            9,
            16,
            SubtitleBurnIn: false,
            VerticalEvidence,
            [
                "speaker-name",
                "material",
                "call-to-action",
                "subtitle",
                "graphic",
            ]),
    ];

    [TestMethod]
    public void AllProfilesLoadWithExactMeasuredContractsAndUniqueIds()
    {
        HashSet<string> profileIds = new(StringComparer.Ordinal);
        HashSet<string> layoutIds = new(StringComparer.Ordinal);
        HashSet<string> templateIds = new(StringComparer.Ordinal);

        foreach (ProfileCase expected in Profiles)
        {
            BundledRenderProfileDefinition definition = LoadSource(expected.RelativePath);

            Assert.AreEqual(expected.ProfileId, definition.Profile.Id.ToString(), expected.RelativePath);
            Assert.AreEqual(expected.LayoutId, definition.Profile.LayoutSpecId.ToString(), expected.RelativePath);
            Assert.AreEqual(expected.TemplateId, definition.Profile.TemplateId.ToString(), expected.RelativePath);
            Assert.AreEqual(expected.Width, definition.Profile.Width, expected.RelativePath);
            Assert.AreEqual(expected.Height, definition.Profile.Height, expected.RelativePath);
            Assert.AreEqual(30000L, definition.Profile.FrameRateNumerator, expected.RelativePath);
            Assert.AreEqual(1001L, definition.Profile.FrameRateDenominator, expected.RelativePath);
            Assert.AreEqual("h264", definition.Profile.VideoCodec, expected.RelativePath);
            Assert.AreEqual("aac", definition.Profile.AudioCodec, expected.RelativePath);
            Assert.AreEqual(expected.SubtitleBurnIn, definition.Profile.SubtitleBurnIn, expected.RelativePath);
            Assert.AreEqual("yuv420p", definition.PixelFormatEvidence, expected.RelativePath);
            Assert.AreEqual(expected.EvidenceSource, definition.EvidenceSource, expected.RelativePath);
            CollectionAssert.AreEqual(
                expected.Capabilities,
                definition.Capabilities.ToArray(),
                expected.RelativePath);
            Assert.IsFalse(definition.PlatformSafeZonesMeasured, expected.RelativePath);
            Assert.AreEqual(
                "development-neutral-placeholder",
                definition.TemplateStatus,
                expected.RelativePath);
            Assert.IsFalse(definition.RequiredExternalBrandAssets, expected.RelativePath);
            CollectionAssert.AreEqual(
                RequiredUnmeasured,
                definition.Unmeasured.ToArray(),
                expected.RelativePath);

            Assert.AreEqual(expected.LayoutId, definition.Layout.Id.ToString(), expected.RelativePath);
            Assert.AreEqual(expected.AspectWidth, definition.Layout.OutputAspectWidth, expected.RelativePath);
            Assert.AreEqual(expected.AspectHeight, definition.Layout.OutputAspectHeight, expected.RelativePath);
            Assert.AreEqual(2, definition.Layout.Regions.Count, expected.RelativePath);
            Assert.AreEqual(1, definition.Layout.SafeZones.Count, expected.RelativePath);
            LayoutRegion region = definition.Layout.Regions[0];
            Assert.AreEqual("primary-video", region.Name, expected.RelativePath);
            Assert.AreEqual(LayoutRegionRole.Video, region.Role, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), region.Rect.X, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), region.Rect.Y, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), region.Rect.Width, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), region.Rect.Height, expected.RelativePath);
            LayoutRegion subtitle = definition.Layout.Regions[1];
            Assert.AreEqual("subtitle-placement", subtitle.Name, expected.RelativePath);
            Assert.AreEqual(LayoutRegionRole.Subtitle, subtitle.Role, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), subtitle.Rect.X, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), subtitle.Rect.Y, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), subtitle.Rect.Width, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), subtitle.Rect.Height, expected.RelativePath);
            SafeZone frameBounds = definition.Layout.SafeZones[0];
            Assert.AreEqual("output-frame-bounds", frameBounds.Name, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), frameBounds.Rect.X, expected.RelativePath);
            Assert.AreEqual(default(RationalNumber), frameBounds.Rect.Y, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), frameBounds.Rect.Width, expected.RelativePath);
            Assert.AreEqual(new RationalNumber(1, 1), frameBounds.Rect.Height, expected.RelativePath);

            Assert.IsTrue(profileIds.Add(definition.Profile.Id.ToString()), expected.RelativePath);
            Assert.IsTrue(layoutIds.Add(definition.Layout.Id.ToString()), expected.RelativePath);
            Assert.IsTrue(templateIds.Add(definition.Profile.TemplateId.ToString()), expected.RelativePath);
        }

        Assert.AreEqual(Profiles.Length, profileIds.Count);
        Assert.AreEqual(Profiles.Length, layoutIds.Count);
        Assert.AreEqual(Profiles.Length, templateIds.Count);
    }

    [TestMethod]
    public void EvidencePointersResolveToMeasuredManifestValuesAndSubtitleCompositions()
    {
        using JsonDocument manifestDocument = JsonDocument.Parse(
            File.ReadAllBytes(ResolveRepositoryPath(
                "benchmarks/fixtures/manual-render-manifest.json")));
        JsonElement manifest = manifestDocument.RootElement;
        JsonElement measuredProfiles = manifest.GetProperty("renderProfiles");
        JsonElement compositions = manifest.GetProperty("compositions");
        JsonElement toolchain = manifest.GetProperty("toolchain");

        foreach (ProfileCase profileCase in Profiles)
        {
            using JsonDocument bundleDocument = JsonDocument.Parse(
                ReadSourceBytes(profileCase.RelativePath));
            JsonElement bundle = bundleDocument.RootElement;
            string evidenceSource = bundle.GetProperty("evidenceSource").GetString()!;
            int evidenceIndex = evidenceSource switch
            {
                LandscapeEvidence => 0,
                VerticalEvidence => 1,
                _ => throw new AssertFailedException("Unexpected evidence pointer."),
            };
            JsonElement measured = measuredProfiles[evidenceIndex];
            JsonElement profile = bundle.GetProperty("profileContent");

            Assert.AreEqual(measured.GetProperty("width").GetInt32(), profile.GetProperty("width").GetInt32());
            Assert.AreEqual(measured.GetProperty("height").GetInt32(), profile.GetProperty("height").GetInt32());
            Assert.AreEqual(measured.GetProperty("pixelFormat").GetString(), profile.GetProperty("pixelFormatEvidence").GetString());
            string[] measuredRate = measured.GetProperty("frameRate").GetString()!.Split('/');
            Assert.AreEqual(
                long.Parse(measuredRate[0], System.Globalization.CultureInfo.InvariantCulture),
                profile.GetProperty("frameRate").GetProperty("numerator").GetInt64());
            Assert.AreEqual(
                long.Parse(measuredRate[1], System.Globalization.CultureInfo.InvariantCulture),
                profile.GetProperty("frameRate").GetProperty("denominator").GetInt64());

            string measuredProfileId = measured.GetProperty("profileId").GetString()!;
            JsonElement composition = compositions
                .EnumerateArray()
                .Single(item => string.Equals(
                    item.GetProperty("profileId").GetString(),
                    measuredProfileId,
                    StringComparison.Ordinal));
            Assert.AreEqual(
                composition.GetProperty("subtitleEnabled").GetBoolean(),
                profile.GetProperty("subtitleBurnIn").GetBoolean());
            Assert.IsTrue(
                toolchain.GetProperty("videoEncoder").GetString()!
                    .StartsWith("h264_", StringComparison.Ordinal));
            Assert.AreEqual("h264", profile.GetProperty("videoCodec").GetString());
            Assert.AreEqual(
                toolchain.GetProperty("audioEncoder").GetString(),
                profile.GetProperty("audioCodec").GetString());
        }
    }

    [TestMethod]
    public void PersistedDigestsMatchCanonicalContentAndAreNotRepeatedPlaceholders()
    {
        foreach (ProfileCase profileCase in Profiles)
        {
            using JsonDocument document = JsonDocument.Parse(
                ReadSourceBytes(profileCase.RelativePath));
            JsonElement root = document.RootElement;

            AssertDigest(root, "profileContent", "profileDigest", profileCase.RelativePath);
            AssertDigest(root, "layoutContent", "layoutDigest", profileCase.RelativePath);
            AssertDigest(
                root,
                "templateContent",
                "templateVersionDigest",
                profileCase.RelativePath);
        }
    }

    [TestMethod]
    public void ProfileJsonIsCopiedBesideTheBuiltAppAssembly()
    {
        string assemblyDirectory = Path.GetDirectoryName(
            typeof(BundledRenderProfileLoader).Assembly.Location)
            ?? throw new AssertFailedException("The App assembly has no directory.");
        string outputRoot = Path.Combine(assemblyDirectory, "Profiles");
        Assert.IsTrue(Directory.Exists(outputRoot), outputRoot);

        string[] copiedRelativePaths = Directory
            .EnumerateFiles(outputRoot, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputRoot, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        string[] expectedRelativePaths = Profiles
            .Select(static profile => profile.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expectedRelativePaths, copiedRelativePaths);

        foreach (ProfileCase profileCase in Profiles)
        {
            string outputPath = Path.Combine(
                outputRoot,
                profileCase.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            CollectionAssert.AreEqual(
                ReadSourceBytes(profileCase.RelativePath),
                File.ReadAllBytes(outputPath),
                profileCase.RelativePath);
            _ = BundledRenderProfileLoader.Load(File.ReadAllBytes(outputPath));
        }
    }

    [TestMethod]
    public void ClosedShapeRejectsExtraMissingAndWrongTypedPropertiesAtEveryLevel()
    {
        AssertMutationRejected(root => root["extra"] = true);
        AssertMutationRejected(root => root.Remove("schemaVersion"));
        AssertMutationRejected(root => root["schemaVersion"] = 1);
        AssertMutationRejected(root => root["profileDigest"] = 42, recomputeDigests: false);

        AssertMutationRejected(root => Profile(root)["extra"] = true);
        AssertMutationRejected(root => Profile(root).Remove("name"));
        AssertMutationRejected(root => Profile(root)["width"] = "1280");
        AssertMutationRejected(root => Profile(root)["capabilities"] = new JsonObject());

        AssertMutationRejected(root => FrameRate(root)["extra"] = true);
        AssertMutationRejected(root => FrameRate(root).Remove("denominator"));
        AssertMutationRejected(root => FrameRate(root)["numerator"] = false);

        AssertMutationRejected(root => Layout(root)["extra"] = true);
        AssertMutationRejected(root => Layout(root).Remove("safeZones"));
        AssertMutationRejected(root => Layout(root)["outputAspectWidth"] = "16");

        AssertMutationRejected(root => PrimaryRegion(root)["extra"] = true);
        AssertMutationRejected(root => PrimaryRegion(root).Remove("role"));
        AssertMutationRejected(root => Rect(root)["extra"] = true);
        AssertMutationRejected(root => Rect(root).Remove("height"));
        AssertMutationRejected(root => Rational(root, "x")["extra"] = true);
        AssertMutationRejected(root => Rational(root, "x")["numerator"] = "0");

        AssertMutationRejected(root => Template(root)["extra"] = true);
        AssertMutationRejected(root => Template(root).Remove("status"));
        AssertMutationRejected(root => Template(root)["requiredExternalBrandAssets"] = 0);

        AssertRejected([0xff]);
    }

    [TestMethod]
    public void AlteredContentWithoutUpdatedDigestIsRejected()
    {
        AssertMutationRejected(
            root => Profile(root)["name"] = "long-form-1280x720-altered",
            recomputeDigests: false);
        AssertMutationRejected(
            root => Template(root)["name"] = "development-neutral-altered",
            recomputeDigests: false);
    }

    [TestMethod]
    public void CrossBoundIdsAndDigestsCannotBeSubstituted()
    {
        AssertMutationRejected(root =>
            Profile(root)["layoutSpecId"] = "11111111-1111-4111-8111-111111111111");
        AssertMutationRejected(root =>
            Profile(root)["templateId"] = "22222222-2222-4222-8222-222222222222");
        AssertMutationRejected(
            root => root["layoutDigest"] = root["profileDigest"]!.DeepClone(),
            recomputeDigests: false);
        AssertMutationRejected(
            root => root["templateVersionDigest"] = root["layoutDigest"]!.DeepClone(),
            recomputeDigests: false);
    }

    [TestMethod]
    public void UppercaseUuidAndDigestSpellingsAreRejected()
    {
        AssertMutationRejected(root =>
            Profile(root)["id"] = Profile(root)["id"]!.GetValue<string>().ToUpperInvariant());
        AssertMutationRejected(
            root => root["profileDigest"] =
                root["profileDigest"]!.GetValue<string>().ToUpperInvariant(),
            recomputeDigests: false);
    }

    [TestMethod]
    public void DuplicatePropertiesAndCapabilitiesAreRejected()
    {
        string json = Encoding.UTF8.GetString(ReadSourceBytes(Profiles[0].RelativePath));
        string duplicateRootProperty = json.Replace(
            "\"schemaVersion\": \"1.0.0\",",
            "\"schemaVersion\": \"1.0.0\",\n  \"schemaVersion\": \"1.0.0\",",
            StringComparison.Ordinal);
        string duplicateNestedProperty = json.Replace(
            "\"name\": \"long-form-1280x720\",",
            "\"name\": \"long-form-1280x720\",\n    \"name\": \"long-form-1280x720\",",
            StringComparison.Ordinal);

        AssertRejected(Encoding.UTF8.GetBytes(duplicateRootProperty));
        AssertRejected(Encoding.UTF8.GetBytes(duplicateNestedProperty));
        AssertMutationRejected(root =>
        {
            JsonArray capabilities = Capabilities(root);
            capabilities.Add(capabilities[0]!.DeepClone());
        });
    }

    [TestMethod]
    public void NonFullFrameRegionsAndUnmeasuredSafeZonesAreRejected()
    {
        AssertMutationRejected(root => Rational(root, "width")["numerator"] = 2);
        AssertMutationRejected(root => Rational(root, "width")["denominator"] = 2);
        AssertMutationRejected(root => PrimaryRegion(root)["name"] = "other-video");
        AssertMutationRejected(root => PrimaryRegion(root)["role"] = "graphic");
        AssertMutationRejected(root => Regions(root).Add(PrimaryRegion(root).DeepClone()));
        AssertMutationRejected(root => SafeZones(root).Add(new JsonObject()));
        AssertMutationRejected(root => SubtitleRegion(root)["name"] = "other-subtitle");
        AssertMutationRejected(root => SubtitleRegion(root)["role"] = "graphic");
        AssertMutationRejected(root => GenericSafeZone(root)["name"] = "platform-safe-zone");
        AssertMutationRejected(root => Profile(root)["platformSafeZonesMeasured"] = true);
    }

    [TestMethod]
    public void CapabilityAndUnmeasuredAllowlistsAreFailClosed()
    {
        AssertMutationRejected(root => Capabilities(root).Add("unknown"));
        AssertMutationRejected(root => Capabilities(root).Clear());
        AssertMutationRejected(root =>
        {
            JsonArray unmeasured = Unmeasured(root);
            string first = unmeasured[0]!.GetValue<string>();
            string second = unmeasured[1]!.GetValue<string>();
            unmeasured[0] = second;
            unmeasured[1] = first;
        });
        AssertMutationRejected(root => Unmeasured(root).RemoveAt(0));
        AssertMutationRejected(root => Unmeasured(root)[0] = "encoder-preset");
    }

    [TestMethod]
    public void EvidenceDimensionsRatePixelFormatSubtitleCodecAndAspectAreFailClosed()
    {
        AssertMutationRejected(root => Profile(root)["width"] = 1920);
        AssertMutationRejected(root => Profile(root)["height"] = 1080);
        AssertMutationRejected(root => FrameRate(root)["numerator"] = 60);
        AssertMutationRejected(root => FrameRate(root)["denominator"] = 1);
        AssertMutationRejected(root => Profile(root)["pixelFormatEvidence"] = "nv12");
        AssertMutationRejected(root => Profile(root)["subtitleBurnIn"] = false);
        AssertMutationRejected(root => Profile(root)["videoCodec"] = "h264_nvenc");
        AssertMutationRejected(root => Profile(root)["audioCodec"] = "pcm_s16le");
        AssertMutationRejected(root => Layout(root)["outputAspectWidth"] = 9);
        AssertMutationRejected(root => Layout(root)["outputAspectHeight"] = 16);
        AssertMutationRejected(
            root => root["evidenceSource"] = VerticalEvidence,
            recomputeDigests: false);
        AssertMutationRejected(
            root => root["evidenceSource"] =
                "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/2",
            recomputeDigests: false);
    }

    [TestMethod]
    public void DevelopmentNeutralTemplateMarkerIsFailClosed()
    {
        AssertMutationRejected(root => Template(root)["name"] = "production-template");
        AssertMutationRejected(root => Template(root)["status"] = "approved");
        AssertMutationRejected(root => Template(root)["requiredExternalBrandAssets"] = true);
    }

    [TestMethod]
    public void PublicApiIsMinimalGetOnlyAndCollectionsAreDefensive()
    {
        Type definitionType = typeof(BundledRenderProfileDefinition);
        Assert.IsTrue(definitionType.IsPublic);
        Assert.IsTrue(definitionType.IsSealed);
        Assert.AreEqual(0, definitionType.GetConstructors().Length);
        PropertyInfo[] properties = definitionType.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Profile",
                "Layout",
                "LayoutDigest",
                "PixelFormatEvidence",
                "Capabilities",
                "PlatformSafeZonesMeasured",
                "EvidenceSource",
                "TemplateStatus",
                "RequiredExternalBrandAssets",
                "Unmeasured",
            },
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.AreEqual(
            typeof(IReadOnlyList<string>),
            definitionType.GetProperty("Capabilities")!.PropertyType);
        Assert.AreEqual(
            typeof(IReadOnlyList<string>),
            definitionType.GetProperty("Unmeasured")!.PropertyType);

        Type loaderType = typeof(BundledRenderProfileLoader);
        Assert.IsTrue(loaderType.IsPublic);
        Assert.IsTrue(loaderType.IsAbstract && loaderType.IsSealed);
        Assert.AreEqual(0, loaderType.GetConstructors().Length);
        MethodInfo[] methods = loaderType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("Load", methods[0].Name);
        Assert.AreEqual(definitionType, methods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(ReadOnlySpan<byte>) },
            methods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        BundledRenderProfileDefinition definition = LoadSource(Profiles[0].RelativePath);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)definition.Capabilities).Add("chapter"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)definition.Unmeasured).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LayoutRegion>)definition.Layout.Regions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SafeZone>)definition.Layout.SafeZones).Add(
                new SafeZone(
                    "unmeasured",
                    new NormalizedRect(
                        default,
                        default,
                        new RationalNumber(1, 1),
                        new RationalNumber(1, 1)))));
    }

    [TestMethod]
    public void LoaderEnforcesExactSixtyFourKibibyteInputLimit()
    {
        byte[] source = ReadSourceBytes(Profiles[0].RelativePath);
        Assert.IsTrue(source.Length < 64 * 1024);

        byte[] atLimit = new byte[64 * 1024];
        source.CopyTo(atLimit, 0);
        Array.Fill(atLimit, (byte)' ', source.Length, atLimit.Length - source.Length);
        _ = BundledRenderProfileLoader.Load(atLimit);

        byte[] overLimit = new byte[(64 * 1024) + 1];
        atLimit.CopyTo(overLimit, 0);
        overLimit[^1] = (byte)' ';
        AssertRejected(overLimit);
        AssertRejected([]);
    }

    [TestMethod]
    public void BundlesContainNoPlatformGeometryOrUnapprovedEncoderTuningFields()
    {
        HashSet<string> prohibitedNames = new(
            [
                "encoder",
                "encoderVariant",
                "videoEncoder",
                "audioEncoder",
                "preset",
                "constantQuality",
                "cq",
                "crf",
                "bitrate",
                "audioBitrate",
                "platformSafeZoneGeometry",
                "safeZoneGeometry",
                "safeZoneVersion",
                "safeZoneSource",
                "safeZoneSourceUrl",
                "subtitleVariantMeasured",
            ],
            StringComparer.OrdinalIgnoreCase);

        foreach (ProfileCase profileCase in Profiles)
        {
            using JsonDocument document = JsonDocument.Parse(
                ReadSourceBytes(profileCase.RelativePath));
            foreach (JsonProperty property in EnumerateProperties(document.RootElement))
            {
                Assert.IsFalse(
                    prohibitedNames.Contains(property.Name),
                    $"{profileCase.RelativePath}: {property.Name}");
            }
        }
    }

    [TestMethod]
    public void StandardFactoryDerivesApprovedLandscapeAndVerticalOutputsWithoutMeasurementOverclaim()
    {
        DefaultSettingsProfile settingsProfile = LoadSettingsProfile();
        DefaultSettingsSnapshot settings = settingsProfile.CreateSnapshot();
        BundledRenderProfileDefinition measuredLandscape = LoadSource(
            "16x9/long-form.json");
        BundledRenderProfileDefinition measuredVertical = LoadSource(
            "9x16/vertical-short.json");

        BundledRenderProfileDefinition landscape =
            StandardRenderProfileFactory.CreateLandscape(
                settingsProfile,
                settings,
                measuredLandscape,
                measuredLandscape.Profile.FrameRate);
        BundledRenderProfileDefinition vertical =
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                settings,
                measuredVertical,
                measuredVertical.Profile.FrameRate);

        Assert.AreEqual(1920, landscape.Profile.Width);
        Assert.AreEqual(1080, landscape.Profile.Height);
        Assert.IsTrue(landscape.Profile.SubtitleBurnIn);
        Assert.AreEqual(1080, vertical.Profile.Width);
        Assert.AreEqual(1920, vertical.Profile.Height);
        Assert.IsTrue(vertical.Profile.SubtitleBurnIn);
        Assert.IsFalse(landscape.PlatformSafeZonesMeasured);
        Assert.IsFalse(vertical.PlatformSafeZonesMeasured);
        Assert.AreEqual(
            "development-neutral-placeholder",
            landscape.TemplateStatus);
        Assert.AreEqual(
            "development-neutral-placeholder",
            vertical.TemplateStatus);
        CollectionAssert.AreEqual(
            RequiredUnmeasured,
            landscape.Unmeasured.ToArray());
        CollectionAssert.AreEqual(
            RequiredUnmeasured,
            vertical.Unmeasured.ToArray());
        StringAssert.StartsWith(
            landscape.EvidenceSource,
            "default-settings-policy:");
        StringAssert.Contains(
            landscape.EvidenceSource,
            measuredLandscape.EvidenceSource);
        StringAssert.StartsWith(
            vertical.EvidenceSource,
            "default-settings-policy:");
        StringAssert.Contains(
            vertical.EvidenceSource,
            measuredVertical.EvidenceSource);
        Assert.IsFalse(landscape.EvidenceSource.StartsWith(
            "benchmarks/fixtures/",
            StringComparison.Ordinal));
        Assert.IsFalse(vertical.EvidenceSource.StartsWith(
            "benchmarks/fixtures/",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void StandardFactoryIsDeterministicAndLeavesMeasuredReferencesUnchanged()
    {
        DefaultSettingsProfile settingsProfile = LoadSettingsProfile();
        DefaultSettingsSnapshot settings = settingsProfile.CreateSnapshot();
        BundledRenderProfileDefinition measured = LoadSource(
            "9x16/vertical-short.json");
        RenderProfile originalProfile = measured.Profile;
        string originalEvidenceSource = measured.EvidenceSource;

        BundledRenderProfileDefinition first =
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                settings,
                measured,
                measured.Profile.FrameRate);
        BundledRenderProfileDefinition second =
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                settings,
                measured,
                measured.Profile.FrameRate);

        Assert.AreEqual(first.Profile.Id, second.Profile.Id);
        Assert.AreEqual(
            first.Profile.ProfileDigest,
            second.Profile.ProfileDigest);
        Assert.AreEqual(first.EvidenceSource, second.EvidenceSource);
        Assert.AreSame(originalProfile, measured.Profile);
        Assert.AreEqual(1080, measured.Profile.Width);
        Assert.AreEqual(1920, measured.Profile.Height);
        Assert.IsFalse(measured.Profile.SubtitleBurnIn);
        Assert.AreEqual(originalEvidenceSource, measured.EvidenceSource);
        Assert.AreNotSame(measured.Profile, first.Profile);
        Assert.AreSame(measured.Layout, first.Layout);
    }

    [TestMethod]
    public void StandardFactoryPreservesExplicitSourceFrameRate()
    {
        DefaultSettingsProfile settingsProfile = LoadSettingsProfile();
        DefaultSettingsSnapshot settings = settingsProfile.CreateSnapshot();
        BundledRenderProfileDefinition measuredLandscape = LoadSource(
            "16x9/long-form.json");
        BundledRenderProfileDefinition measuredVertical = LoadSource(
            "9x16/vertical-short.json");
        var sourceFrameRate = new RationalNumber(25, 1);

        BundledRenderProfileDefinition landscape =
            StandardRenderProfileFactory.CreateLandscape(
                settingsProfile,
                settings,
                measuredLandscape,
                sourceFrameRate);
        BundledRenderProfileDefinition vertical =
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                settings,
                measuredVertical,
                sourceFrameRate);

        Assert.AreEqual(sourceFrameRate, landscape.Profile.FrameRate);
        Assert.AreEqual(sourceFrameRate, vertical.Profile.FrameRate);
        Assert.AreNotEqual(
            measuredLandscape.Profile.ProfileDigest,
            landscape.Profile.ProfileDigest);
        Assert.AreNotEqual(
            measuredVertical.Profile.ProfileDigest,
            vertical.Profile.ProfileDigest);
    }

    [TestMethod]
    public void StandardFactoryRejectsWrongReferenceKindAndAlteredClosedPolicy()
    {
        DefaultSettingsProfile settingsProfile = LoadSettingsProfile();
        DefaultSettingsSnapshot settings = settingsProfile.CreateSnapshot();
        BundledRenderProfileDefinition measuredLandscape = LoadSource(
            "16x9/long-form.json");
        BundledRenderProfileDefinition measuredVertical = LoadSource(
            "9x16/vertical-short.json");
        DefaultSettingsSnapshot altered = settingsProfile.CreateSnapshot(
            settings.Values.Select(selection => string.Equals(
                    selection.Key,
                    "subtitles.verticalBurnInEnabled",
                    StringComparison.Ordinal)
                ? new DefaultSettingSelection(
                    selection.Key,
                    DefaultSettingValue.FromBoolean(false))
                : selection).ToArray());

        Assert.ThrowsExactly<ArgumentException>(() =>
            StandardRenderProfileFactory.CreateLandscape(
                settingsProfile,
                settings,
                measuredVertical,
                measuredVertical.Profile.FrameRate));
        Assert.ThrowsExactly<ArgumentException>(() =>
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                settings,
                measuredLandscape,
                measuredLandscape.Profile.FrameRate));
        Assert.ThrowsExactly<ArgumentException>(() =>
            StandardRenderProfileFactory.CreateVerticalShort(
                settingsProfile,
                altered,
                measuredVertical,
                measuredVertical.Profile.FrameRate));
    }

    // FR-058 AC-033.1: a portrait source keeps a portrait 1080x1920 long form only when the settings say followSource;
    // settings without the key (profiles before 1.2.0) and landscape sources keep the 1920x1080 long form.
    [TestMethod]
    [TestCategory("FR-058")]
    public void LongFormFollowsAPortraitSourceOnlyWithTheOrientationSetting()
    {
        DefaultSettingsProfile latest = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(
            ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.2.0.json")));
        DefaultSettingsProfile previous = LoadSettingsProfile();
        BundledRenderProfileDefinition measuredLandscape = LoadSource("16x9/long-form.json");
        BundledRenderProfileDefinition measuredVertical = LoadSource("9x16/vertical-short.json");
        var rate = new RationalNumber(30, 1);
        var portrait = new SourceDisplayGeometry(1080, 1920);
        var landscapeSource = new SourceDisplayGeometry(1920, 1080);

        BundledRenderProfileDefinition followed = StandardRenderProfileFactory.CreateLongForm(
            latest, latest.CreateSnapshot(), measuredLandscape, measuredVertical, rate, portrait);
        Assert.IsTrue(StandardRenderProfileFactory.IsPortraitLongForm(followed));
        Assert.AreEqual(StandardRenderProfileFactory.PortraitLongFormName, followed.Profile.Name);
        Assert.AreEqual(1080, followed.Profile.Width);
        Assert.AreEqual(1920, followed.Profile.Height);
        Assert.IsTrue(followed.Profile.SubtitleBurnIn);
        Assert.AreSame(measuredVertical.Layout, followed.Layout);
        CollectionAssert.AreEqual(measuredLandscape.Capabilities.ToArray(), followed.Capabilities.ToArray());
        Assert.AreEqual(followed.Profile.ProfileDigest, StandardRenderProfileFactory.CreateLongForm(
            latest, latest.CreateSnapshot(), measuredLandscape, measuredVertical, rate, portrait).Profile.ProfileDigest);

        DefaultSettingsSnapshot alwaysLandscape = latest.CreateSnapshot(latest.CreateSnapshot().Values.Select(item =>
            item.Key == "output.longFormOrientation" ? new DefaultSettingSelection(item.Key, DefaultSettingValue.FromText("landscape")) : item).ToArray());
        foreach (BundledRenderProfileDefinition landscape in new[]
        {
            StandardRenderProfileFactory.CreateLongForm(latest, latest.CreateSnapshot(), measuredLandscape, measuredVertical, rate, landscapeSource),
            StandardRenderProfileFactory.CreateLongForm(latest, alwaysLandscape, measuredLandscape, measuredVertical, rate, portrait),
            StandardRenderProfileFactory.CreateLongForm(previous, previous.CreateSnapshot(), measuredLandscape, measuredVertical, rate, portrait),
        })
        {
            Assert.IsFalse(StandardRenderProfileFactory.IsPortraitLongForm(landscape));
            Assert.AreEqual(1920, landscape.Profile.Width);
            Assert.AreEqual(1080, landscape.Profile.Height);
        }

        // The pre-1.2.0 long form is byte-for-byte the profile CreateLandscape always produced.
        Assert.AreEqual(
            StandardRenderProfileFactory.CreateLandscape(previous, previous.CreateSnapshot(), measuredLandscape, rate).Profile.ProfileDigest,
            StandardRenderProfileFactory.CreateLongForm(previous, previous.CreateSnapshot(), measuredLandscape, measuredVertical, rate, portrait).Profile.ProfileDigest);
        Assert.ThrowsExactly<ArgumentException>(() => StandardRenderProfileFactory.CreateLongForm(
            latest, latest.CreateSnapshot(), measuredVertical, measuredVertical, rate, portrait));
    }

    private static BundledRenderProfileDefinition LoadSource(string relativePath) =>
        BundledRenderProfileLoader.Load(ReadSourceBytes(relativePath));

    private static DefaultSettingsProfile LoadSettingsProfile() =>
        BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(
            ResolveRepositoryPath(
                "src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));

    private static byte[] ReadSourceBytes(string relativePath) =>
        File.ReadAllBytes(ResolveRepositoryPath(
            $"src/HubRadio.App/Profiles/{relativePath}"));

    private static void AssertDigest(
        JsonElement root,
        string contentProperty,
        string digestProperty,
        string profilePath)
    {
        string persisted = root.GetProperty(digestProperty).GetString()!;
        string actual = ContentDigest
            .Compute(CanonicalJson.Write(root.GetProperty(contentProperty)))
            .Hex;

        Assert.AreEqual(persisted, actual, $"{profilePath}: {digestProperty}");
        Assert.AreEqual(64, persisted.Length, $"{profilePath}: {digestProperty}");
        Assert.IsTrue(
            persisted.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{profilePath}: {digestProperty}");
        Assert.IsFalse(
            IsRepeatedPattern(persisted),
            $"{profilePath}: {digestProperty} resembles a placeholder pattern.");
    }

    private static bool IsRepeatedPattern(string value)
    {
        for (int period = 1; period <= value.Length / 2; period++)
        {
            if (value.Length % period == 0 &&
                value.Select((character, index) => character == value[index % period]).All(static equal => equal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertMutationRejected(
        Action<JsonObject> mutation,
        bool recomputeDigests = true)
    {
        JsonObject root = ParseSourceObject(Profiles[0].RelativePath);
        mutation(root);
        if (recomputeDigests)
        {
            RecomputeDigests(root);
        }

        AssertRejected(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void AssertRejected(byte[] utf8Json)
    {
        Assert.Throws<JsonException>(() => BundledRenderProfileLoader.Load(utf8Json));
    }

    private static JsonObject ParseSourceObject(string relativePath) =>
        JsonNode.Parse(ReadSourceBytes(relativePath))?.AsObject()
        ?? throw new AssertFailedException("The profile fixture root was null.");

    private static void RecomputeDigests(JsonObject root)
    {
        RecomputeDigest(root, "profileContent", "profileDigest");
        RecomputeDigest(root, "layoutContent", "layoutDigest");
        RecomputeDigest(root, "templateContent", "templateVersionDigest");
    }

    private static void RecomputeDigest(
        JsonObject root,
        string contentProperty,
        string digestProperty)
    {
        JsonNode content = root[contentProperty]
            ?? throw new AssertFailedException($"Missing test content '{contentProperty}'.");
        root[digestProperty] = CanonicalJson.ComputeDigest(
            Encoding.UTF8.GetBytes(content.ToJsonString())).Hex;
    }

    private static JsonObject Profile(JsonObject root) =>
        RequireObject(root, "profileContent");

    private static JsonObject FrameRate(JsonObject root) =>
        RequireObject(Profile(root), "frameRate");

    private static JsonArray Capabilities(JsonObject root) =>
        RequireArray(Profile(root), "capabilities");

    private static JsonObject Layout(JsonObject root) =>
        RequireObject(root, "layoutContent");

    private static JsonArray Regions(JsonObject root) =>
        RequireArray(Layout(root), "regions");

    private static JsonObject PrimaryRegion(JsonObject root) =>
        Regions(root)[0]?.AsObject()
        ?? throw new AssertFailedException("Missing primary region.");

    private static JsonObject SubtitleRegion(JsonObject root) =>
        Regions(root)[1]?.AsObject()
        ?? throw new AssertFailedException("Missing subtitle region.");

    private static JsonObject Rect(JsonObject root) =>
        RequireObject(PrimaryRegion(root), "rect");

    private static JsonObject Rational(JsonObject root, string propertyName) =>
        RequireObject(Rect(root), propertyName);

    private static JsonArray SafeZones(JsonObject root) =>
        RequireArray(Layout(root), "safeZones");

    private static JsonObject GenericSafeZone(JsonObject root) =>
        SafeZones(root)[0]?.AsObject()
        ?? throw new AssertFailedException("Missing generic safe zone.");

    private static JsonObject Template(JsonObject root) =>
        RequireObject(root, "templateContent");

    private static JsonArray Unmeasured(JsonObject root) =>
        RequireArray(root, "unmeasured");

    private static JsonObject RequireObject(JsonObject parent, string propertyName) =>
        parent[propertyName]?.AsObject()
        ?? throw new AssertFailedException($"Missing test object '{propertyName}'.");

    private static JsonArray RequireArray(JsonObject parent, string propertyName) =>
        parent[propertyName]?.AsArray()
        ?? throw new AssertFailedException($"Missing test array '{propertyName}'.");

    private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                yield return property;
                foreach (JsonProperty descendant in EnumerateProperties(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                foreach (JsonProperty descendant in EnumerateProperties(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative file '{relativePath}'.",
                        path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }

    private sealed record ProfileCase(
        string RelativePath,
        string ProfileId,
        string LayoutId,
        string TemplateId,
        int Width,
        int Height,
        int AspectWidth,
        int AspectHeight,
        bool SubtitleBurnIn,
        string EvidenceSource,
        string[] Capabilities);
}