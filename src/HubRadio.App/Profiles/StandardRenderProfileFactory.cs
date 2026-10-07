using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;

namespace HubRadio.App.Profiles;

public static class StandardRenderProfileFactory
{
    private const string DevelopmentTemplateStatus =
        "development-neutral-placeholder";
    private const string PixelFormat = "yuv420p";
    private const string VideoCodec = "h264";
    private const string AudioCodec = "aac";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] RequiredUnmeasured =
    [
        "brand-assets",
        "cross-gpu-reproducibility",
        "platform-safe-zone-geometry",
        "production-quality-thresholds",
    ];

    private static readonly ReferenceExpectation LandscapeReference = new(
        EvidenceSource:
            "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/0",
        ProfileId: "14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa",
        ProfileName: "long-form-1280x720",
        ProfileDigest:
            "3b507050583bd9909f8503b9bec50fb644bcd6a62d055b7c87ed58b986b5a0fe",
        LayoutId: "d95a6c34-359a-5c6d-8ed8-15ffd94966bc",
        LayoutDigest:
            "c1ab78336b68342dc7595666648d1b9f2f6848c2141af1c9e9a990e7cea605a9",
        TemplateId: "bf08c776-0f97-5373-a091-c70b60acd569",
        TemplateName: "development-neutral-long-form",
        TemplateDigest:
            "2189c7f2f01cbb2b4b2f99b3067622b149a69ab2ca737d17462ea55dbf67b7aa",
        Width: 1280,
        Height: 720,
        AspectWidth: 16,
        AspectHeight: 9,
        SubtitleBurnIn: true,
        Capabilities:
        [
            "chapter",
            "topic-display",
            "speaker-name",
            "material",
            "call-to-action",
            "end-card-margin",
            "subtitle",
            "graphic",
        ]);

    private static readonly ReferenceExpectation VerticalReference = new(
        EvidenceSource:
            "benchmarks/fixtures/manual-render-manifest.json#/renderProfiles/1",
        ProfileId: "c04c1910-d1da-5752-a35b-cc8b9e4604d3",
        ProfileName: "vertical-short-1080x1920",
        ProfileDigest:
            "32d0a2831c004616345a53000a131c7ce6b85519a8256fef6e6ae8d9c19aa15d",
        LayoutId: "f18766a0-78c9-5d08-83e1-ee812cccdcb6",
        LayoutDigest:
            "14af7880751c9696bfa743ed3aeedcbd00c6c7e800b90ac059c0bee386fef1be",
        TemplateId: "2d41bbd4-6891-51f5-b59f-78e01b37ff02",
        TemplateName: "development-neutral-vertical-short",
        TemplateDigest:
            "d91368a556a6ea094ec1303170a11a0ab6e6c29a026969f091dddea56741e81e",
        Width: 1080,
        Height: 1920,
        AspectWidth: 9,
        AspectHeight: 16,
        SubtitleBurnIn: false,
        Capabilities:
        [
            "speaker-name",
            "material",
            "call-to-action",
            "subtitle",
            "graphic",
        ]);

    public static BundledRenderProfileDefinition CreateLandscape(
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        BundledRenderProfileDefinition measuredReference,
        RationalNumber sourceFrameRate) =>
        Create(
            settingsProfile,
            settings,
            measuredReference,
            sourceFrameRate,
            LandscapeReference,
            "standard-landscape-1920x1080-burned-in",
            width: 1920,
            height: 1080,
            subtitleBurnIn: true,
            variant: "landscape");

    public static BundledRenderProfileDefinition CreateVerticalShort(
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        BundledRenderProfileDefinition measuredReference,
        RationalNumber sourceFrameRate) =>
        Create(
            settingsProfile,
            settings,
            measuredReference,
            sourceFrameRate,
            VerticalReference,
            "standard-vertical-short-1080x1920-burned-in",
            width: 1080,
            height: 1920,
            subtitleBurnIn: true,
            variant: "vertical-short");

    /// <summary>FR-058: the long-form profile name used when a portrait source keeps its orientation.</summary>
    public const string PortraitLongFormName = "standard-portrait-long-form-1080x1920-burned-in";

    internal const string LongFormOrientationKey = "output.longFormOrientation";

    /// <summary>
    /// FR-058: the long-form profile for this source. A portrait source keeps a portrait 1080x1920
    /// long form only when the accepted settings say <c>followSource</c>; settings without the key
    /// (profiles before 1.2.0) always build the 1920x1080 landscape long form, so saved results replay.
    /// </summary>
    public static BundledRenderProfileDefinition CreateLongForm(
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        BundledRenderProfileDefinition measuredLandscapeReference,
        BundledRenderProfileDefinition measuredVerticalReference,
        RationalNumber sourceFrameRate,
        SourceDisplayGeometry sourceGeometry) =>
        UsesPortraitLongForm(settings, sourceGeometry)
            ? CreatePortraitLongForm(settingsProfile, settings, measuredLandscapeReference,
                measuredVerticalReference, sourceFrameRate)
            : CreateLandscape(settingsProfile, settings, measuredLandscapeReference, sourceFrameRate);

    public static bool UsesPortraitLongForm(DefaultSettingsSnapshot settings, SourceDisplayGeometry sourceGeometry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sourceGeometry);
        return sourceGeometry.Height > sourceGeometry.Width && settings.Values.Any(static item =>
            item.Key == LongFormOrientationKey &&
            item.Value.Kind == DefaultSettingValueKind.Text &&
            item.Value.TextValue == "followSource");
    }

    /// <summary>FR-058: true for the portrait long-form profile produced by <see cref="CreateLongForm"/>.</summary>
    public static bool IsPortraitLongForm(BundledRenderProfileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.Equals(definition.Profile.Name, PortraitLongFormName, StringComparison.Ordinal)
            && definition.Profile.Width == 1080
            && definition.Profile.Height == 1920
            && definition.Layout.OutputAspectWidth == 9
            && definition.Layout.OutputAspectHeight == 16;
    }

    /// <summary>
    /// FR-058: the measured 9:16 layout and neutral template of the vertical reference, rendered as a
    /// long form. The long-form element capabilities (chapters, topic display, end-card margin) are
    /// taken from the measured landscape long-form reference, which is validated as well.
    /// </summary>
    internal static BundledRenderProfileDefinition CreatePortraitLongForm(
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        BundledRenderProfileDefinition measuredLandscapeReference,
        BundledRenderProfileDefinition measuredVerticalReference,
        RationalNumber sourceFrameRate)
    {
        ArgumentNullException.ThrowIfNull(measuredLandscapeReference);
        ValidateReference(measuredLandscapeReference, LandscapeReference);
        return Create(
            settingsProfile,
            settings,
            measuredVerticalReference,
            sourceFrameRate,
            VerticalReference,
            PortraitLongFormName,
            width: 1080,
            height: 1920,
            subtitleBurnIn: true,
            variant: "portrait-long-form",
            capabilities: measuredLandscapeReference.Capabilities);
    }

    private static BundledRenderProfileDefinition Create(
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        BundledRenderProfileDefinition measuredReference,
        RationalNumber sourceFrameRate,
        ReferenceExpectation expectation,
        string name,
        int width,
        int height,
        bool subtitleBurnIn,
        string variant,
        IReadOnlyList<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(settingsProfile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(measuredReference);

        ValidateSettings(settingsProfile, settings);
        ValidateReference(measuredReference, expectation);
        if (sourceFrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFrameRate));
        }

        string evidenceSource = string.Concat(
            "default-settings-policy:",
            settingsProfile.Version,
            "/",
            settingsProfile.ContentDigest.Hex,
            "/",
            settings.ContentDigest.Hex,
            ";measured-reference:",
            measuredReference.EvidenceSource);
        string[] seedFields =
        [
            variant,
            settingsProfile.Id.ToString(),
            settingsProfile.Version.ToString(),
            settingsProfile.ContentDigest.Hex,
            settings.ContentDigest.Hex,
            measuredReference.Profile.Id.ToString(),
            measuredReference.Profile.ProfileDigest.Hex,
            measuredReference.LayoutDigest.Hex,
            measuredReference.TemplateReference.VersionDigest.Hex,
            sourceFrameRate.Numerator.ToString(CultureInfo.InvariantCulture),
            sourceFrameRate.Denominator.ToString(CultureInfo.InvariantCulture),
            evidenceSource,
        ];
        ContentDigest idSeed = ComputeDigest(
            "hubradio.standard-render-profile-id-seed.v1",
            seedFields);
        EntityId<RenderProfileIdTag> id = DeterministicId<RenderProfileIdTag>(
            idSeed,
            variant);
        ContentDigest profileDigest = ComputeDigest(
            "hubradio.standard-render-profile.v1",
            seedFields.Concat(
            [
                id.ToString(),
                name,
                width.ToString(CultureInfo.InvariantCulture),
                height.ToString(CultureInfo.InvariantCulture),
                sourceFrameRate.Numerator.ToString(
                    CultureInfo.InvariantCulture),
                sourceFrameRate.Denominator.ToString(
                    CultureInfo.InvariantCulture),
                measuredReference.Profile.LayoutSpecId.ToString(),
                measuredReference.Profile.TemplateId.ToString(),
                VideoCodec,
                AudioCodec,
                subtitleBurnIn.ToString(CultureInfo.InvariantCulture),
            ]));
        var profile = new RenderProfile(
            id,
            name,
            profileDigest,
            width,
            height,
            sourceFrameRate.Numerator,
            sourceFrameRate.Denominator,
            measuredReference.Profile.LayoutSpecId,
            measuredReference.Profile.TemplateId,
            measuredReference.Profile.TemplateVersionDigest,
            VideoCodec,
            AudioCodec,
            subtitleBurnIn);
        return new BundledRenderProfileDefinition(
            profile,
            measuredReference.Layout,
            measuredReference.LayoutDigest,
            measuredReference.TemplateReference,
            measuredReference.PixelFormatEvidence,
            capabilities ?? measuredReference.Capabilities,
            false,
            evidenceSource,
            measuredReference.TemplateStatus,
            requiredExternalBrandAssets: false,
            measuredReference.Unmeasured);
    }

    private static void ValidateSettings(
        DefaultSettingsProfile profile,
        DefaultSettingsSnapshot settings)
    {
        DefaultSettingsSnapshot restored;
        try
        {
            restored = DefaultSettingsSnapshot.ParseCanonical(
                settings.CanonicalJson,
                profile);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The settings snapshot does not bind to its profile.",
                nameof(settings),
                exception);
        }

        if (settings.ProfileId != profile.Id
            || settings.ProfileVersion != profile.Version
            || settings.ProfileDigest != profile.ContentDigest
            || restored.ContentDigest != settings.ContentDigest
            || !string.Equals(
                restored.CanonicalJson,
                settings.CanonicalJson,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The settings snapshot does not bind to its profile.",
                nameof(settings));
        }

        IReadOnlyDictionary<string, DefaultSettingValue> values =
            settings.Values.ToDictionary(
                static selection => selection.Key,
                static selection => selection.Value,
                StringComparer.Ordinal);
        RequireText(values, "project.publicationScope", "internalOnly");
        RequireText(values, "project.template", "builtInNeutralNoLogo");
        RequireText(values, "output.landscapeResolution", "1920x1080");
        RequireText(values, "output.verticalResolution", "1080x1920");
        RequireText(values, "output.container", "MP4");
        RequireText(values, "output.videoEncoding", "h264HighProgressive420");
        RequireText(values, "output.audioCodec", "AAC-LC");
        RequireInteger(values, "output.audioSampleRate", 48000);
        RequireText(values, "output.frameRatePolicy", "sourcePreserve");
        RequireText(values, "output.sdrColorSpace", "BT.709");
        RequireText(values, "output.externalPlatformCompliance", "notClaimed");
        RequireTextList(
            values,
            "subtitles.landscapeVariants",
            ["burnIn", "clean", "SRT", "VTT"]);
        RequireBoolean(values, "subtitles.verticalBurnInEnabled", true);
        RequireText(values, "subtitles.platformCompliance", "notClaimed");
        RequireText(values, "subtitles.platformUiMaskStatus", "unmeasured");
    }

    private static void ValidateReference(
        BundledRenderProfileDefinition definition,
        ReferenceExpectation expected)
    {
        RenderProfile profile = definition.Profile;
        bool valid = string.Equals(
                definition.EvidenceSource,
                expected.EvidenceSource,
                StringComparison.Ordinal)
            && string.Equals(
                profile.Id.ToString(),
                expected.ProfileId,
                StringComparison.Ordinal)
            && string.Equals(
                profile.Name,
                expected.ProfileName,
                StringComparison.Ordinal)
            && string.Equals(
                profile.ProfileDigest.Hex,
                expected.ProfileDigest,
                StringComparison.Ordinal)
            && string.Equals(
                definition.Layout.Id.ToString(),
                expected.LayoutId,
                StringComparison.Ordinal)
            && string.Equals(
                definition.LayoutDigest.Hex,
                expected.LayoutDigest,
                StringComparison.Ordinal)
            && string.Equals(
                definition.TemplateReference.Id.ToString(),
                expected.TemplateId,
                StringComparison.Ordinal)
            && string.Equals(
                definition.TemplateReference.Name,
                expected.TemplateName,
                StringComparison.Ordinal)
            && string.Equals(
                definition.TemplateReference.VersionDigest.Hex,
                expected.TemplateDigest,
                StringComparison.Ordinal)
            && profile.Width == expected.Width
            && profile.Height == expected.Height
            && profile.FrameRateNumerator == 30000
            && profile.FrameRateDenominator == 1001
            && profile.SubtitleBurnIn == expected.SubtitleBurnIn
            && profile.LayoutSpecId == definition.Layout.Id
            && profile.TemplateId == definition.TemplateReference.Id
            && profile.TemplateVersionDigest
                == definition.TemplateReference.VersionDigest
            && definition.Layout.OutputAspectWidth == expected.AspectWidth
            && definition.Layout.OutputAspectHeight == expected.AspectHeight
            && string.Equals(profile.VideoCodec, VideoCodec, StringComparison.Ordinal)
            && string.Equals(profile.AudioCodec, AudioCodec, StringComparison.Ordinal)
            && string.Equals(
                definition.PixelFormatEvidence,
                PixelFormat,
                StringComparison.Ordinal)
            && !definition.PlatformSafeZonesMeasured
            && string.Equals(
                definition.TemplateStatus,
                DevelopmentTemplateStatus,
                StringComparison.Ordinal)
            && !definition.RequiredExternalBrandAssets
            && definition.Capabilities.SequenceEqual(
                expected.Capabilities,
                StringComparer.Ordinal)
            && definition.Unmeasured.SequenceEqual(
                RequiredUnmeasured,
                StringComparer.Ordinal);
        if (!valid)
        {
            throw new ArgumentException(
                "The measured render-profile reference is not the approved profile.",
                nameof(definition));
        }
    }

    private static void RequireText(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        string expected)
    {
        if (!values.TryGetValue(key, out DefaultSettingValue? value)
            || value.Kind != DefaultSettingValueKind.Text
            || !string.Equals(value.TextValue, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Setting '{key}' does not match the standard render policy.",
                nameof(values));
        }
    }

    private static void RequireInteger(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        long expected)
    {
        if (!values.TryGetValue(key, out DefaultSettingValue? value)
            || value is null
            || value.Kind != DefaultSettingValueKind.Integer
            || value.IntegerValue != expected)
        {
            throw new ArgumentException(
                $"Setting '{key}' does not match the standard render policy.",
                nameof(values));
        }
    }

    private static void RequireBoolean(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        bool expected)
    {
        if (!values.TryGetValue(key, out DefaultSettingValue? value)
            || value is null
            || value.Kind != DefaultSettingValueKind.Boolean
            || value.BooleanValue != expected)
        {
            throw new ArgumentException(
                $"Setting '{key}' does not match the standard render policy.",
                nameof(values));
        }
    }

    private static void RequireTextList(
        IReadOnlyDictionary<string, DefaultSettingValue> values,
        string key,
        IReadOnlyList<string> expected)
    {
        if (!values.TryGetValue(key, out DefaultSettingValue? value)
            || value is null
            || value.Kind != DefaultSettingValueKind.TextList
            || !value.TextListValue.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Setting '{key}' does not match the standard render policy.",
                nameof(values));
        }
    }

    private static ContentDigest ComputeDigest(
        string domain,
        IEnumerable<string> fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendField(hash, tag: 1, domain);
        foreach (string field in fields)
        {
            AppendField(hash, tag: 2, field);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static EntityId<TTag> DeterministicId<TTag>(
        ContentDigest seed,
        string label)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendField(hash, tag: 1, "hubradio.standard-render-profile-id.v1");
        AppendField(hash, tag: 2, seed.Hex);
        AppendField(hash, tag: 3, label);
        byte[] bytes = hash.GetHashAndReset();
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return EntityId<TTag>.From(new Guid(guidBytes));
    }

    private static void AppendField(
        IncrementalHash hash,
        byte tag,
        string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        hash.AppendData(header);
        hash.AppendData(bytes);
    }

    private sealed record ReferenceExpectation(
        string EvidenceSource,
        string ProfileId,
        string ProfileName,
        string ProfileDigest,
        string LayoutId,
        string LayoutDigest,
        string TemplateId,
        string TemplateName,
        string TemplateDigest,
        int Width,
        int Height,
        int AspectWidth,
        int AspectHeight,
        bool SubtitleBurnIn,
        string[] Capabilities);
}