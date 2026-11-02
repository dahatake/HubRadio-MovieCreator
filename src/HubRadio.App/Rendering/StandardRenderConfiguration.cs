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

    /// <summary>FR-015: true only when the user enabled <c>editing.longOpenerEnabled</c> (default false).</summary>
    public bool LongOpenerEnabled { get; private init; }

    /// <summary>FR-015: true only when the user enabled <c>editing.shortOpenerEnabled</c> (default false).</summary>
    public bool ShortOpenerEnabled { get; private init; }

    /// <summary>FR-015: the permitted logo / title / performers / jingle the enabled opener uses; null when none was supplied.</summary>
    public StandardOpenerInput? Opener { get; private set; }

    internal StandardRenderConfiguration WithOpener(StandardOpenerInput? opener)
    {
        var copy = (StandardRenderConfiguration)MemberwiseClone();
        copy.Opener = opener;
        return copy;
    }

    /// <summary>FR-136: the production template the Job applies; null keeps the FR-059/FR-060 default output unchanged.</summary>
    public StyleTemplates.StyleTemplateDefinition? StyleTemplate { get => styleTemplate; init => styleTemplate = value; }

    private StyleTemplates.StyleTemplateDefinition? styleTemplate;

    /// <summary>
    /// FR-137 (6): the registered characters (standing pictures) and the folder holding their image files. Used only with a template
    /// that has the dialogue free telop; the app bundles no picture.
    /// </summary>
    public IReadOnlyList<StyleTemplates.CharacterPortrait> Characters { get; private set; } = [];

    public string? PortraitImageDirectory { get; private set; }

    /// <summary>FR-137 (6): the dialogue lines (text, time, speaker character ID) of the character dialogue.</summary>
    public IReadOnlyList<StyleTemplates.StyleTemplateFreeTelopInput> DialogueLines { get; private set; } = [];

    /// <summary>FR-140: the speaker assignment of the Job (only a confirmed one reaches the output); null assigns nobody.</summary>
    public StyleTemplates.CueSpeakerAssignmentSet? SpeakerAssignments { get; private set; }

    /// <summary>FR-142: the timed ASR tokens by ID, from which the words (and their times) of each displayed cue are taken.</summary>
    public IReadOnlyDictionary<HubRadio.Core.Identity.EntityId<HubRadio.Core.Provenance.TranscriptTokenIdTag>, HubRadio.Core.Transcripts.Token>? WordTokens { get; private set; }

    /// <summary>Receives the record of what the template put on the cues (written by the caller into the Job directory).</summary>
    public Action<StyleTemplates.StyleTemplateRenderRecord>? RecordSink { get; private set; }

    /// <summary>FR-141: what the user entered or confirmed for the talk-show side telops and name bands (null: nothing).</summary>
    public StyleTemplates.TalkShowInput? TalkShow { get; private set; }

    /// <summary>FR-143: the user's explicit adjustment that moves the vertical subtitle into the bottom margin.</summary>
    public bool SubtitleIntoMargin { get; private set; }

    internal StandardRenderConfiguration WithTalkShow(StyleTemplates.TalkShowInput? input, bool subtitleIntoMargin)
    {
        var copy = (StandardRenderConfiguration)MemberwiseClone();
        copy.TalkShow = input;
        copy.SubtitleIntoMargin = subtitleIntoMargin;
        return copy;
    }

    internal StandardRenderConfiguration WithCueDetails(StyleTemplates.CueSpeakerAssignmentSet? assignments,
        IReadOnlyDictionary<HubRadio.Core.Identity.EntityId<HubRadio.Core.Provenance.TranscriptTokenIdTag>, HubRadio.Core.Transcripts.Token>? tokens,
        Action<StyleTemplates.StyleTemplateRenderRecord>? recordSink)
    {
        var copy = (StandardRenderConfiguration)MemberwiseClone();
        copy.SpeakerAssignments = assignments;
        copy.WordTokens = tokens;
        copy.RecordSink = recordSink;
        return copy;
    }

    /// <summary>The same configuration with the registered characters and the dialogue lines of a character dialogue.</summary>
    internal StandardRenderConfiguration WithCharacterDialogue(IReadOnlyList<StyleTemplates.CharacterPortrait> characters,
        string? imageDirectory, IReadOnlyList<StyleTemplates.StyleTemplateFreeTelopInput> lines)
    {
        var copy = (StandardRenderConfiguration)MemberwiseClone();
        copy.Characters = characters ?? [];
        copy.PortraitImageDirectory = imageDirectory;
        copy.DialogueLines = lines ?? [];
        return copy;
    }

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
        // FR-015 / AC-063: the openers are off by default and only a user's explicit opt-in turns them on.
        bool longOpener = Value("editing.longOpenerEnabled").BooleanValue
            ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        bool shortOpener = Value("editing.shortOpenerEnabled").BooleanValue
            ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        foreach (string key in new[] { "editing.crossfadeEnabled",
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
                LongOpenerEnabled = longOpener,
                ShortOpenerEnabled = shortOpener,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedSetting);
        }
    }
}