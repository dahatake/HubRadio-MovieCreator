using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

/// <summary>Resolved settings, not an alternative source of editorial metadata.</summary>
public sealed class StandardRenderConfiguration
{
    // QC still enforces audio.truePeakMaximum. AAC encoding measured slightly above the
    // loudnorm true-peak target, so the filter aims lower (benchmarks/results/initial-pilot.md §5.3.3).
    private const decimal LoudnormTruePeakHeadroomDb = 0.1m;

    private StandardRenderConfiguration(ContentDigest settingsDigest, FfmpegEncoderSettings encoder,
        RationalTime titleDuration, RationalTime endDuration, string callToAction,
        (int Width, int Height) landscapeThumbnail, (int Width, int Height) verticalThumbnail)
    {
        SettingsSnapshotDigest = settingsDigest;
        Encoder = encoder;
        TitleMaximumDuration = titleDuration;
        EndOverlayMaximumDuration = endDuration;
        CallToActionText = callToAction;
        LandscapeThumbnailWidth = landscapeThumbnail.Width;
        LandscapeThumbnailHeight = landscapeThumbnail.Height;
        VerticalThumbnailWidth = verticalThumbnail.Width;
        VerticalThumbnailHeight = verticalThumbnail.Height;
    }

    public ContentDigest SettingsSnapshotDigest { get; }
    public FfmpegEncoderSettings Encoder { get; }
    public RationalTime TitleMaximumDuration { get; }
    public RationalTime EndOverlayMaximumDuration { get; }
    public string CallToActionText { get; }
    public int LandscapeThumbnailWidth { get; }
    public int LandscapeThumbnailHeight { get; }
    public int VerticalThumbnailWidth { get; }
    public int VerticalThumbnailHeight { get; }

    /// <summary>FR-059: few/standard/many, or null for profiles before 1.2.0 (no emphasis telops).</summary>
    public string? EmphasisTelopAmount { get; private init; }

    /// <summary>FR-060: true only when the accepted settings enable sound effects (profile 1.2.0 or later).</summary>
    public bool SoundEffectsEnabled { get; private init; }

    /// <summary>FR-136: the production template the Job applies; null keeps the FR-059/FR-060 default output unchanged.</summary>
    public StyleTemplates.StyleTemplateDefinition? StyleTemplate { get => styleTemplate; init => styleTemplate = value; }

    private StyleTemplates.StyleTemplateDefinition? styleTemplate;

    /// <summary>The same resolved settings with a production template (null removes it).</summary>
    internal StandardRenderConfiguration WithStyleTemplate(StyleTemplates.StyleTemplateDefinition? template)
    {
        var copy = (StandardRenderConfiguration)MemberwiseClone();
        copy.styleTemplate = template;
        return copy;
    }

    public static StandardRenderConfiguration FromSettings(DefaultSettingsProfile profile,
        DefaultSettingsSnapshot settings, RationalNumber sourceFrameRate, int sourceAudioChannels,
        StyleTemplates.StyleTemplateDefinition? styleTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        if (DefaultSettingsSnapshot.ParseCanonical(settings.CanonicalJson, profile).ContentDigest != settings.ContentDigest)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        var values = settings.Values.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        DefaultSettingValue Value(string key) => values.TryGetValue(key, out DefaultSettingValue? value)
            ? value : throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        long Integer(string key) => Value(key).IntegerValue
            ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        void RequireText(string key, string expected)
        {
            if (Value(key).TextValue != expected)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        }
        foreach (string key in new[] { "editing.longOpenerEnabled", "editing.shortOpenerEnabled", "editing.crossfadeEnabled",
            "audio.noiseReductionEnabled", "audio.humReductionEnabled", "audio.dialogueBackgroundMusicEnabled", "assets.musicLoopEnabled" })
        {
            if (Value(key).BooleanValue != false)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        }
        foreach (string key in new[] { "subtitles.enabled", "subtitles.verticalBurnInEnabled", "thumbnails.sourceFrameOnly" })
        {
            if (Value(key).BooleanValue != true)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        }
        RequireText("output.videoEncoding", "h264HighProgressive420");
        RequireText("output.audioCodec", "AAC-LC");
        RequireText("output.sdrColorSpace", "BT.709");
        RequireText("output.frameRatePolicy", "sourcePreserve");
        RequireText("output.container", "MP4");
        RequireText("editing.primaryTransition", "hardCut");
        RequireText("audio.quickStartLoudnessProfile", "internalOnline");
        // The existing channel policy preserves mono/stereo. Do not silently
        // downmix multichannel audio without a separately approved mapping.
        if (sourceAudioChannels is not (1 or 2) || Integer("output.audioSampleRate") != 48000
            || sourceFrameRate.Numerator <= 0 || sourceFrameRate > new RationalNumber(60, 1)
            || !Value("output.audioChannelPolicy").Equals(profile.GetDefinition("output.audioChannelPolicy").DefaultValue)
            || !Value("editing.callToActionTexts").TextListValue.SequenceEqual(new[] { "本編を見る" }, StringComparer.Ordinal))
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);

        RationalTime Duration(string key, long maximum)
        {
            DefaultSettingValue value = Value(key);
            RationalTime duration = value.IntegerValue is long integer ? new RationalTime(integer, 1)
                : value.RationalValue is RationalNumber rational ? new RationalTime(rational.Numerator, rational.Denominator)
                : throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
            if (duration <= default(RationalTime) || duration > new RationalTime(maximum, 1))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
            return duration;
        }

        (int Width, int Height) Canvas(string key, int aspectWidth, int aspectHeight)
        {
            string[] parts = (Value(key).TextValue ?? "").Split('x');
            if (parts.Length != 2 || parts.Any(static part => part.Length == 0 || part.Any(static c => c is < '0' or > '9'))
                || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)
                || width is < 2 or > 4096 || height is < 2 or > 4096 || width % 2 != 0 || height % 2 != 0
                || (long)width * aspectHeight != (long)height * aspectWidth)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
            return (width, height);
        }

        try
        {
            string? telops = values.TryGetValue("subtitles.emphasisTelopAmount", out DefaultSettingValue? amount)
                ? amount.TextValue is "few" or "standard" or "many" ? amount.TextValue
                    : throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting)
                : null;
            bool soundEffects = values.TryGetValue("audio.soundEffectsEnabled", out DefaultSettingValue? effects)
                && (effects.BooleanValue ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting));
            return new StandardRenderConfiguration(settings.ContentDigest,
                new FfmpegEncoderSettings(checked((int)Integer(sourceFrameRate > new RationalNumber(30, 1)
                    ? "output.highFrameRateBitrate" : "output.standardFrameRateBitrate")),
                    checked((int)Integer(sourceAudioChannels == 1 ? "output.monoBitrate" : "output.stereoBitrate")),
                    sourceAudioChannels, new FfmpegLoudnessTarget(Integer("audio.internalOnlineTargetLoudness"),
                        Integer("audio.truePeakMaximum") - LoudnormTruePeakHeadroomDb)),
                Duration("editing.shortHookMaximumStart", 5), Duration("output.endScreenDuration", 20),
                Value("editing.callToActionTexts").TextListValue.Single(),
                Canvas("thumbnails.landscapeCanvas", 16, 9), Canvas("thumbnails.verticalCanvas", 9, 16))
            {
                EmphasisTelopAmount = telops,
                SoundEffectsEnabled = soundEffects,
                StyleTemplate = styleTemplate,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        }
    }
}