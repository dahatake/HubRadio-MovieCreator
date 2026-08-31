using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;

namespace HubRadio.App.Profiles;

public sealed class BundledRenderProfileDefinition
{
    internal BundledRenderProfileDefinition(
        RenderProfile profile,
        LayoutSpec layout,
        ContentDigest layoutDigest,
        TemplateReference templateReference,
        string pixelFormatEvidence,
        IReadOnlyList<string> capabilities,
        bool platformSafeZonesMeasured,
        string evidenceSource,
        string templateStatus,
        bool requiredExternalBrandAssets,
        IReadOnlyList<string> unmeasured)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(layoutDigest);
        ArgumentNullException.ThrowIfNull(templateReference);
        ArgumentNullException.ThrowIfNull(pixelFormatEvidence);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(evidenceSource);
        ArgumentNullException.ThrowIfNull(templateStatus);
        ArgumentNullException.ThrowIfNull(unmeasured);

        Profile = profile;
        Layout = layout;
        LayoutDigest = layoutDigest;
        TemplateReference = templateReference;
        PixelFormatEvidence = pixelFormatEvidence;
        Capabilities = CopyAsReadOnly(capabilities, nameof(capabilities));
        PlatformSafeZonesMeasured = platformSafeZonesMeasured;
        EvidenceSource = evidenceSource;
        TemplateStatus = templateStatus;
        RequiredExternalBrandAssets = requiredExternalBrandAssets;
        Unmeasured = CopyAsReadOnly(unmeasured, nameof(unmeasured));
    }

    public RenderProfile Profile { get; }

    public LayoutSpec Layout { get; }

    public ContentDigest LayoutDigest { get; }

    internal TemplateReference TemplateReference { get; }

    public string PixelFormatEvidence { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public bool PlatformSafeZonesMeasured { get; }

    public string EvidenceSource { get; }

    public string TemplateStatus { get; }

    public bool RequiredExternalBrandAssets { get; }

    public IReadOnlyList<string> Unmeasured { get; }

    private static IReadOnlyList<string> CopyAsReadOnly(
        IReadOnlyList<string> values,
        string parameterName)
    {
        string[] copy = new string[values.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = values[index]
                ?? throw new ArgumentException(
                    "The collection must not contain null values.",
                    parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public static class BundledRenderProfileLoader
{
    private const int MaximumUtf8JsonBytes = 64 * 1024;
    private const string SchemaVersion = "1.0.0";
    private const string LandscapeEvidenceSource =
        "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/0";
    private const string VerticalEvidenceSource =
        "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/1";
    private const string PixelFormatEvidence = "yuv420p";
    private const string VideoCodec = "h264";
    private const string AudioCodec = "aac";
    private const string PrimaryVideoRegionName = "primary-video";
    private const string SubtitlePlacementRegionName = "subtitle-placement";
    private const string OutputFrameBoundsSafeZoneName = "output-frame-bounds";
    private const string DevelopmentNeutralPrefix = "development-neutral-";
    private const string DevelopmentNeutralStatus = "development-neutral-placeholder";

    private static readonly string[] RootProperties =
    [
        "schemaVersion",
        "evidenceSource",
        "profileContent",
        "profileDigest",
        "layoutContent",
        "layoutDigest",
        "templateContent",
        "templateVersionDigest",
        "unmeasured",
    ];

    private static readonly string[] ProfileProperties =
    [
        "id",
        "name",
        "width",
        "height",
        "frameRate",
        "videoCodec",
        "audioCodec",
        "subtitleBurnIn",
        "pixelFormatEvidence",
        "layoutSpecId",
        "templateId",
        "capabilities",
        "platformSafeZonesMeasured",
    ];

    private static readonly string[] FrameRateProperties =
    [
        "numerator",
        "denominator",
    ];

    private static readonly string[] LayoutProperties =
    [
        "id",
        "outputAspectWidth",
        "outputAspectHeight",
        "regions",
        "safeZones",
    ];

    private static readonly string[] RegionProperties =
    [
        "name",
        "rect",
        "role",
    ];

    private static readonly string[] RectProperties =
    [
        "x",
        "y",
        "width",
        "height",
    ];

    private static readonly string[] RationalProperties =
    [
        "numerator",
        "denominator",
    ];

    private static readonly string[] TemplateProperties =
    [
        "id",
        "name",
        "status",
        "requiredExternalBrandAssets",
    ];

    private static readonly HashSet<string> CapabilityAllowlist = new(
        [
            "chapter",
            "topic-display",
            "speaker-name",
            "material",
            "call-to-action",
            "end-card-margin",
            "subtitle",
            "graphic",
        ],
        StringComparer.Ordinal);

    private static readonly string[] RequiredUnmeasured =
    [
        "brand-assets",
        "cross-gpu-reproducibility",
        "platform-safe-zone-geometry",
        "production-quality-thresholds",
    ];

    public static BundledRenderProfileDefinition Load(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new JsonException("A bundled render profile must not be empty.");
        }

        if (utf8Json.Length > MaximumUtf8JsonBytes)
        {
            throw new JsonException(
                $"A bundled render profile must not exceed {MaximumUtf8JsonBytes} UTF-8 bytes.");
        }

        byte[] canonicalJson = CanonicalJson.CanonicalizeToUtf8(utf8Json);
        using JsonDocument document = JsonDocument.Parse(canonicalJson);
        JsonElement root = document.RootElement;
        RequireExactObject(root, "bundle", RootProperties);

        string schemaVersion = RequireString(root, "schemaVersion", "bundle");
        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("The bundled render-profile schema version is not supported.");
        }

        string evidenceSource = RequireString(root, "evidenceSource", "bundle");
        EvidenceExpectation evidence = evidenceSource switch
        {
            LandscapeEvidenceSource => new EvidenceExpectation(
                Width: 1280,
                Height: 720,
                AspectWidth: 16,
                AspectHeight: 9,
                SubtitleBurnIn: true),
            VerticalEvidenceSource => new EvidenceExpectation(
                Width: 1080,
                Height: 1920,
                AspectWidth: 9,
                AspectHeight: 16,
                SubtitleBurnIn: false),
            _ => throw new JsonException(
                "The evidence source must identify an approved manual-render profile."),
        };

        JsonElement profileContent = RequireProperty(root, "profileContent", "bundle");
        RequireExactObject(profileContent, "profileContent", ProfileProperties);

        EntityId<RenderProfileIdTag> profileId = RequireEntityId<RenderProfileIdTag>(
            profileContent,
            "id",
            "profileContent");
        string profileName = RequireNonBlankString(
            profileContent,
            "name",
            "profileContent");
        int width = RequireInt32(profileContent, "width", "profileContent");
        int height = RequireInt32(profileContent, "height", "profileContent");
        if (width != evidence.Width || height != evidence.Height)
        {
            throw new JsonException(
                "The profile dimensions do not match the selected manual-render evidence.");
        }

        JsonElement frameRate = RequireProperty(
            profileContent,
            "frameRate",
            "profileContent");
        RequireExactObject(frameRate, "profileContent.frameRate", FrameRateProperties);
        long frameRateNumerator = RequireInt64(
            frameRate,
            "numerator",
            "profileContent.frameRate");
        long frameRateDenominator = RequireInt64(
            frameRate,
            "denominator",
            "profileContent.frameRate");
        if (frameRateNumerator != 30000 || frameRateDenominator != 1001)
        {
            throw new JsonException(
                "The frame rate does not match the selected manual-render evidence.");
        }

        RequireExactString(profileContent, "videoCodec", "profileContent", VideoCodec);
        RequireExactString(profileContent, "audioCodec", "profileContent", AudioCodec);
        bool subtitleBurnIn = RequireBoolean(
            profileContent,
            "subtitleBurnIn",
            "profileContent");
        if (subtitleBurnIn != evidence.SubtitleBurnIn)
        {
            throw new JsonException(
                "The subtitle variant does not match the measured manual-render composition.");
        }

        RequireExactString(
            profileContent,
            "pixelFormatEvidence",
            "profileContent",
            PixelFormatEvidence);
        EntityId<LayoutSpecIdTag> layoutSpecId = RequireEntityId<LayoutSpecIdTag>(
            profileContent,
            "layoutSpecId",
            "profileContent");
        EntityId<TemplateIdTag> templateId = RequireEntityId<TemplateIdTag>(
            profileContent,
            "templateId",
            "profileContent");
        string[] capabilities = ReadCapabilities(
            RequireProperty(profileContent, "capabilities", "profileContent"));
        bool platformSafeZonesMeasured = RequireBoolean(
            profileContent,
            "platformSafeZonesMeasured",
            "profileContent");
        if (platformSafeZonesMeasured)
        {
            throw new JsonException(
                "Platform-specific safe-zone geometry has not been measured.");
        }

        ContentDigest profileDigest = RequireDigest(root, "profileDigest", "bundle");
        VerifyDigest(profileContent, profileDigest, "profileContent");

        JsonElement layoutContent = RequireProperty(root, "layoutContent", "bundle");
        RequireExactObject(layoutContent, "layoutContent", LayoutProperties);
        EntityId<LayoutSpecIdTag> layoutId = RequireEntityId<LayoutSpecIdTag>(
            layoutContent,
            "id",
            "layoutContent");
        if (layoutId != layoutSpecId)
        {
            throw new JsonException(
                "The profile layoutSpecId must match the embedded layoutContent id.");
        }

        int outputAspectWidth = RequireInt32(
            layoutContent,
            "outputAspectWidth",
            "layoutContent");
        int outputAspectHeight = RequireInt32(
            layoutContent,
            "outputAspectHeight",
            "layoutContent");
        if (outputAspectWidth != evidence.AspectWidth ||
            outputAspectHeight != evidence.AspectHeight)
        {
            throw new JsonException(
                "The layout aspect does not match the selected manual-render evidence.");
        }

        if ((BigInteger)width * outputAspectHeight !=
            (BigInteger)height * outputAspectWidth)
        {
            throw new JsonException(
                "The profile dimensions and layout aspect must be exactly equal.");
        }

        LayoutRegion[] regions = ReadLayoutRegions(
            RequireProperty(layoutContent, "regions", "layoutContent"));
        JsonElement safeZones = RequireProperty(layoutContent, "safeZones", "layoutContent");
        SafeZone genericSafeZone = ReadGenericSafeZone(safeZones);

        ContentDigest layoutDigest = RequireDigest(root, "layoutDigest", "bundle");
        VerifyDigest(layoutContent, layoutDigest, "layoutContent");

        JsonElement templateContent = RequireProperty(root, "templateContent", "bundle");
        RequireExactObject(templateContent, "templateContent", TemplateProperties);
        EntityId<TemplateIdTag> embeddedTemplateId = RequireEntityId<TemplateIdTag>(
            templateContent,
            "id",
            "templateContent");
        if (embeddedTemplateId != templateId)
        {
            throw new JsonException(
                "The profile templateId must match the embedded templateContent id.");
        }

        string templateName = RequireNonBlankString(
            templateContent,
            "name",
            "templateContent");
        if (!templateName.StartsWith(DevelopmentNeutralPrefix, StringComparison.Ordinal) ||
            templateName.Length == DevelopmentNeutralPrefix.Length)
        {
            throw new JsonException(
                "The bundled placeholder template must have a development-neutral name.");
        }

        RequireExactString(
            templateContent,
            "status",
            "templateContent",
            DevelopmentNeutralStatus);
        bool requiredExternalBrandAssets = RequireBoolean(
            templateContent,
            "requiredExternalBrandAssets",
            "templateContent");
        if (requiredExternalBrandAssets)
        {
            throw new JsonException(
                "The development-neutral placeholder must not require external brand assets.");
        }

        ContentDigest templateVersionDigest = RequireDigest(
            root,
            "templateVersionDigest",
            "bundle");
        VerifyDigest(templateContent, templateVersionDigest, "templateContent");

        string[] unmeasured = ReadRequiredUnmeasured(
            RequireProperty(root, "unmeasured", "bundle"));

        var layout = new LayoutSpec(
            layoutId,
            outputAspectWidth,
            outputAspectHeight,
            regions,
            [genericSafeZone]);
        var profile = new RenderProfile(
            profileId,
            profileName,
            profileDigest,
            width,
            height,
            frameRateNumerator,
            frameRateDenominator,
            layoutSpecId,
            templateId,
            templateVersionDigest,
            VideoCodec,
            AudioCodec,
            subtitleBurnIn);

        return new BundledRenderProfileDefinition(
            profile,
            layout,
            layoutDigest,
            new TemplateReference(
                embeddedTemplateId,
                templateName,
                templateVersionDigest),
            PixelFormatEvidence,
            capabilities,
            platformSafeZonesMeasured,
            evidenceSource,
            DevelopmentNeutralStatus,
            requiredExternalBrandAssets,
            unmeasured);
    }

    private static LayoutRegion[] ReadLayoutRegions(JsonElement regions)
    {
        if (regions.ValueKind != JsonValueKind.Array || regions.GetArrayLength() != 2)
        {
            throw new JsonException(
                "The layout must contain full-frame video and subtitle-placement regions.");
        }

        return
        [
            ReadFullFrameRegion(
                regions[0],
                "layoutContent.regions[0]",
                PrimaryVideoRegionName,
                "video",
                LayoutRegionRole.Video),
            ReadFullFrameRegion(
                regions[1],
                "layoutContent.regions[1]",
                SubtitlePlacementRegionName,
                "subtitle",
                LayoutRegionRole.Subtitle),
        ];
    }

    private static LayoutRegion ReadFullFrameRegion(
        JsonElement region,
        string context,
        string expectedName,
        string expectedRole,
        LayoutRegionRole role)
    {
        RequireExactObject(region, context, RegionProperties);
        RequireExactString(
            region,
            "name",
            context,
            expectedName);
        RequireExactString(region, "role", context, expectedRole);

        JsonElement rect = RequireProperty(
            region,
            "rect",
            context);
        NormalizedRect fullFrame = ReadFullFrameRect(rect, $"{context}.rect");

        return new LayoutRegion(
            expectedName,
            fullFrame,
            role);
    }

    private static SafeZone ReadGenericSafeZone(JsonElement safeZones)
    {
        if (safeZones.ValueKind != JsonValueKind.Array || safeZones.GetArrayLength() != 1)
        {
            throw new JsonException(
                "The layout must contain exactly one generic output-frame safe zone.");
        }

        JsonElement safeZone = safeZones[0];
        RequireExactObject(
            safeZone,
            "layoutContent.safeZones[0]",
            ["name", "rect"]);
        RequireExactString(
            safeZone,
            "name",
            "layoutContent.safeZones[0]",
            OutputFrameBoundsSafeZoneName);
        JsonElement rect = RequireProperty(
            safeZone,
            "rect",
            "layoutContent.safeZones[0]");
        return new SafeZone(
            OutputFrameBoundsSafeZoneName,
            ReadFullFrameRect(rect, "layoutContent.safeZones[0].rect"));
    }

    private static NormalizedRect ReadFullFrameRect(
        JsonElement rect,
        string context)
    {
        RequireExactObject(rect, context, RectProperties);
        RationalNumber x = ReadExactRational(rect, context, "x", 0, 1);
        RationalNumber y = ReadExactRational(rect, context, "y", 0, 1);
        RationalNumber width = ReadExactRational(rect, context, "width", 1, 1);
        RationalNumber height = ReadExactRational(rect, context, "height", 1, 1);
        return new NormalizedRect(x, y, width, height);
    }

    private static RationalNumber ReadExactRational(
        JsonElement parent,
        string parentContext,
        string propertyName,
        long expectedNumerator,
        long expectedDenominator)
    {
        string context = $"{parentContext}.{propertyName}";
        JsonElement value = RequireProperty(
            parent,
            propertyName,
            parentContext);
        RequireExactObject(value, context, RationalProperties);
        long numerator = RequireInt64(value, "numerator", context);
        long denominator = RequireInt64(value, "denominator", context);
        if (numerator != expectedNumerator || denominator != expectedDenominator)
        {
            throw new JsonException(
                $"The {context} value must use the canonical full-frame coordinate.");
        }

        return new RationalNumber(numerator, denominator);
    }

    private static string[] ReadCapabilities(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() == 0)
        {
            throw new JsonException("Capabilities must be a nonempty array.");
        }

        string[] values = new string[capabilities.GetArrayLength()];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement element in capabilities.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not string value)
            {
                throw new JsonException("Every capability must be a string.");
            }

            if (!CapabilityAllowlist.Contains(value))
            {
                throw new JsonException($"Capability '{value}' is not allowed.");
            }

            if (!seen.Add(value))
            {
                throw new JsonException("Capabilities must be unique.");
            }

            values[index++] = value;
        }

        return values;
    }

    private static string[] ReadRequiredUnmeasured(JsonElement unmeasured)
    {
        if (unmeasured.ValueKind != JsonValueKind.Array ||
            unmeasured.GetArrayLength() != RequiredUnmeasured.Length)
        {
            throw new JsonException(
                "The unmeasured array must contain every required limitation.");
        }

        string[] values = new string[RequiredUnmeasured.Length];
        int index = 0;
        foreach (JsonElement element in unmeasured.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not string value)
            {
                throw new JsonException("Every unmeasured limitation must be a string.");
            }

            if (!string.Equals(value, RequiredUnmeasured[index], StringComparison.Ordinal))
            {
                throw new JsonException(
                    "Unmeasured limitations must be unique, complete, and ordinally sorted.");
            }

            values[index++] = value;
        }

        return values;
    }

    private static void VerifyDigest(
        JsonElement content,
        ContentDigest expected,
        string context)
    {
        ContentDigest actual = ContentDigest.Compute(CanonicalJson.Write(content));
        byte[] expectedBytes = Convert.FromHexString(expected.Hex);
        byte[] actualBytes = Convert.FromHexString(actual.Hex);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new JsonException($"The {context} digest does not match its canonical content.");
        }
    }

    private static EntityId<TTag> RequireEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string value = RequireString(parent, propertyName, context);
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a nonempty lowercase UUID in D format.");
        }

        return id;
    }

    private static ContentDigest RequireDigest(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string value = RequireString(parent, propertyName, context);
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a lowercase SHA-256 digest.");
        }

        return digest;
    }

    private static void RequireExactObject(
        JsonElement value,
        string context,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The {context} value must be an object.");
        }

        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException(
                    $"Property '{property.Name}' is not allowed in {context}.");
            }
        }

        if (count != expectedProperties.Count)
        {
            throw new JsonException($"The {context} object is missing a required property.");
        }
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonException(
                $"The {context} object is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new JsonException($"The {context}.{propertyName} value must be a string.");
        }

        return text;
    }

    private static string RequireNonBlankString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string value = RequireString(parent, propertyName, context);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be nonblank without surrounding whitespace.");
        }

        return value;
    }

    private static void RequireExactString(
        JsonElement parent,
        string propertyName,
        string context,
        string expected)
    {
        string value = RequireString(parent, propertyName, context);
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value is not supported.");
        }
    }

    private static int RequireInt32(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out decimal number) ||
            number != decimal.Truncate(number) ||
            number < int.MinValue ||
            number > int.MaxValue)
        {
            throw new JsonException($"The {context}.{propertyName} value must be an integer.");
        }

        return decimal.ToInt32(number);
    }

    private static long RequireInt64(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out decimal number) ||
            number != decimal.Truncate(number) ||
            number < long.MinValue ||
            number > long.MaxValue)
        {
            throw new JsonException($"The {context}.{propertyName} value must be an integer.");
        }

        return decimal.ToInt64(number);
    }

    private static bool RequireBoolean(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"The {context}.{propertyName} value must be a boolean.");
        }

        return value.GetBoolean();
    }

    private readonly record struct EvidenceExpectation(
        int Width,
        int Height,
        int AspectWidth,
        int AspectHeight,
        bool SubtitleBurnIn);
}