using System.Text;
using System.Text.Json;
using HubRadio.Core.Layout;
using HubRadio.Core.Settings;

namespace HubRadio.App.DefaultSettings;

public static class BundledDefaultSettingsProfileLoader
{
    private const int MaximumUtf8JsonBytes = 64 * 1024;
    private const string ExpectedId = "8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b";
    private const string ExpectedName = "hubradio-production-defaults";
    private static readonly IReadOnlyDictionary<string, string> ApprovedDigestsByVersion =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.0.0"] = "5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c",
            ["1.1.0"] = "6f0bde7c863572d9e41b6b18e9f2a223a9864f80255334d35fa10f9d1fa3ee31",
        };
    private const string CallToActionDescription =
        "A short uses the single text 本編を見る only when a corresponding long-form version exists; otherwise it has no call to action. No URL or other action is inferred.";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] ExpectedKeys =
    [
        "assets.musicEligibleSections",
        "assets.musicLibraryFailurePolicy",
        "assets.musicLibraryPolicy",
        "assets.musicLoopEnabled",
        "assets.musicSelectionPolicy",
        "assets.musicUsageScope",
        "audio.broadcastTargetLoudness",
        "audio.dialogueBackgroundMusicEnabled",
        "audio.dialogueDuckAmountWhenEnabled",
        "audio.humReductionEnabled",
        "audio.humReductionSuggestionEnabled",
        "audio.internalOnlineLoudnessTolerance",
        "audio.internalOnlineTargetLoudness",
        "audio.loudnessMeasurementStandard",
        "audio.musicalFitEvaluation",
        "audio.noiseReductionEnabled",
        "audio.noiseReductionSuggestionEnabled",
        "audio.perceptualSpeechUnderstanding",
        "audio.quickStartLoudnessProfile",
        "audio.truePeakMaximum",
        "editing.adjacentExactRepetitionAutoExclude",
        "editing.callToActionTexts",
        "editing.contextDeviationAutoRemovalEnabled",
        "editing.coughAutoRemovalEnabled",
        "editing.crossfadeDurationWhenEnabled",
        "editing.crossfadeEnabled",
        "editing.explicitCutAutoExclude",
        "editing.hardCutDuration",
        "editing.intensity",
        "editing.jlCutPolicy",
        "editing.jumpCutFallbackPolicy",
        "editing.longOpenerEnabled",
        "editing.noiseAutoRemovalEnabled",
        "editing.primaryTransition",
        "editing.repetitionMatchPolicy",
        "editing.shortHookMaximumStart",
        "editing.shortHookPolicy",
        "editing.shortOpenerEnabled",
        "editing.silenceMinimumDuration",
        "editing.silencePadding",
        "editing.silenceThreshold",
        "editing.topicBoundaryTransition",
        "evaluation.defaultMode",
        "evaluation.humanPassClaimEnabled",
        "evaluation.selfReviewPolicy",
        "output.audioChannelPolicy",
        "output.audioCodec",
        "output.audioSampleRate",
        "output.container",
        "output.directPostingEnabled",
        "output.endScreenDuration",
        "output.endScreenMinimumSourceDuration",
        "output.externalPlatformCompliance",
        "output.frameRatePolicy",
        "output.hdrPolicy",
        "output.hdrWarningRequired",
        "output.highFrameRateBitrate",
        "output.landscapeResolution",
        "output.monoBitrate",
        "output.sdrColorSpace",
        "output.shortClassificationMaximumDuration",
        "output.shortTargetDuration",
        "output.standardFrameRateBitrate",
        "output.stereoBitrate",
        "output.verticalResolution",
        "output.videoEncoding",
        "privacy.cloudPayloadPolicy",
        "privacy.cloudTextSuggestionEnabled",
        "privacy.crossAccountGuarantee",
        "privacy.externalPublicationEnabled",
        "privacy.userCount",
        "privacy.userScope",
        "project.anonymousSpeakerPolicy",
        "project.languagePolicy",
        "project.publicationDeadlinePolicy",
        "project.publicationScope",
        "project.quickStartArtifactSet",
        "project.requiredSourceVideoCount",
        "project.startSummaryRequired",
        "project.template",
        "project.verticalShortCandidateCount",
        "storage.autoDeleteEnabled",
        "storage.backupDefaultDestination",
        "storage.backupMode",
        "storage.workspacePath",
        "subtitles.backgroundColor",
        "subtitles.backgroundOpaque",
        "subtitles.cueGapMinimum",
        "subtitles.cueGapPolicy",
        "subtitles.cueMaximumDuration",
        "subtitles.cueMinimumDuration",
        "subtitles.enabled",
        "subtitles.foregroundColor",
        "subtitles.horizontalLineLengthMaximum",
        "subtitles.landscapeLineHeight",
        "subtitles.landscapeRegion",
        "subtitles.landscapeVariants",
        "subtitles.lineBreakPolicy",
        "subtitles.lineCountMaximum",
        "subtitles.platformCompliance",
        "subtitles.platformUiMaskFallback",
        "subtitles.platformUiMaskStatus",
        "subtitles.platformUiMaskWarningRequired",
        "subtitles.primaryProfile",
        "subtitles.readingSpeedMaximum",
        "subtitles.semanticCompleteness",
        "subtitles.translationCueMinimumDuration",
        "subtitles.translationHorizontalLineLengthMaximum",
        "subtitles.translationLineCountMaximum",
        "subtitles.translationProfile",
        "subtitles.translationReadingSpeedMaximum",
        "subtitles.translationVerticalLineLengthMaximum",
        "subtitles.verticalBurnInEnabled",
        "subtitles.verticalLineHeight",
        "subtitles.verticalLineLengthMaximum",
        "subtitles.verticalRegion",
        "thumbnails.candidateCount",
        "thumbnails.generatedFaceEnabled",
        "thumbnails.generatedSceneEnabled",
        "thumbnails.landscapeCanvas",
        "thumbnails.sourceFrameOnly",
        "thumbnails.textPolicy",
        "thumbnails.verticalCanvas",
    ];

    internal static bool IsApprovedProfile(DefaultSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ApprovedDigestsByVersion.TryGetValue(profile.Version.ToString(), out string? digest) &&
            string.Equals(profile.ContentDigest.Hex, digest, StringComparison.Ordinal) &&
            string.Equals(profile.Id.ToString(), ExpectedId, StringComparison.Ordinal);
    }

    public static DefaultSettingsProfile Load(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new JsonException("A bundled default-settings profile must not be empty.");
        }

        if (utf8Json.Length > MaximumUtf8JsonBytes)
        {
            throw new JsonException(
                $"A bundled default-settings profile must not exceed {MaximumUtf8JsonBytes} UTF-8 bytes.");
        }

        string canonicalJson;
        try
        {
            canonicalJson = StrictUtf8.GetString(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException(
                "A bundled default-settings profile must use strict UTF-8.",
                exception);
        }

        DefaultSettingsProfile profile;
        try
        {
            profile = DefaultSettingsProfile.ParseCanonical(canonicalJson);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "The bundled default-settings profile is invalid.",
                exception);
        }

        ValidateIdentity(profile);
        ValidateInventory(profile);
        ValidateEvidence(profile);
        ValidateCriticalSemantics(profile);
        if (!ApprovedDigestsByVersion.TryGetValue(
                profile.Version.ToString(),
                out string? approvedDigest) ||
            !string.Equals(
                profile.ContentDigest.Hex,
                approvedDigest,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                "The bundled default-settings profile differs from the approved content for its version.");
        }

        return profile;
    }

    private static void ValidateIdentity(DefaultSettingsProfile profile)
    {
        if (!string.Equals(profile.Id.ToString(), ExpectedId, StringComparison.Ordinal) ||
            !string.Equals(profile.Name, ExpectedName, StringComparison.Ordinal) ||
            !ApprovedDigestsByVersion.ContainsKey(profile.Version.ToString()))
        {
            throw new JsonException(
                "The bundled default-settings profile identity is not approved.");
        }
    }

    private static void ValidateInventory(DefaultSettingsProfile profile)
    {
        string[] actualKeys = profile.Definitions
            .Select(static definition => definition.Key)
            .ToArray();
        if (!actualKeys.SequenceEqual(ExpectedKeys, StringComparer.Ordinal))
        {
            throw new JsonException(
                "The bundled default-settings profile key inventory is not approved.");
        }

        RequireGroupCount(profile, DefaultSettingGroup.AudioAndAssets, 20);
        RequireGroupCount(profile, DefaultSettingGroup.Editing, 22);
        RequireGroupCount(profile, DefaultSettingGroup.PrivacyAndStorage, 13);
        RequireGroupCount(profile, DefaultSettingGroup.ProjectAndOutput, 30);
        RequireGroupCount(profile, DefaultSettingGroup.SubtitlesAndGraphics, 38);
    }

    private static void RequireGroupCount(
        DefaultSettingsProfile profile,
        DefaultSettingGroup group,
        int expectedCount)
    {
        if (profile.Definitions.Count(definition => definition.Group == group) != expectedCount)
        {
            throw new JsonException(
                $"The bundled default-settings profile {group} inventory is not approved.");
        }
    }

    private static void ValidateEvidence(DefaultSettingsProfile profile)
    {
        foreach (DefaultSettingDefinition definition in profile.Definitions)
        {
            foreach (DefaultSettingEvidence evidence in definition.Evidence)
            {
                if (!IsApprovedEvidence(evidence))
                {
                    throw new JsonException(
                        $"Setting '{definition.Key}' references evidence outside the approved registry.");
                }
            }
        }
    }

    private static bool IsApprovedEvidence(DefaultSettingEvidence evidence) =>
        (evidence.Kind, evidence.SourceId) switch
        {
            (DefaultSettingEvidenceKind.CurrentImplementation,
                "C02" or "C03" or "C04" or "C05") => true,
            (DefaultSettingEvidenceKind.OfficialRecommendation,
                "R01" or "R02" or "R03" or "R06" or "R08" or "R09" or
                "R10" or "R12" or "R13" or "R15" or "R17" or "R18" or
                "R19" or "R20") => true,
            (DefaultSettingEvidenceKind.PeerReviewedFinding, "R22" or "R23") => true,
            (DefaultSettingEvidenceKind.PracticalGuidance, "R24" or "R25" or "R26") => true,
            (DefaultSettingEvidenceKind.ProductDecision, "productDecision") => true,
            (DefaultSettingEvidenceKind.Specification,
                "R04" or "R05" or "R07" or "R11" or "R14" or "R16") => true,
            (DefaultSettingEvidenceKind.UserDecision, "U01") => true,
            _ => false,
        };

    private static void ValidateCriticalSemantics(DefaultSettingsProfile profile)
    {
        RequireText(profile, "output.landscapeResolution", "1920x1080");
        RequireText(profile, "output.verticalResolution", "1080x1920");
        RequireText(profile, "output.container", "MP4");
        RequireText(profile, "output.videoEncoding", "h264HighProgressive420");
        RequireText(profile, "output.audioCodec", "AAC-LC");
        RequireInteger(profile, "output.audioSampleRate", 48000, DefaultSettingUnit.Hertz);
        RequireText(profile, "output.sdrColorSpace", "BT.709");
        RequireText(profile, "output.frameRatePolicy", "sourcePreserve");
        RequireText(profile, "output.hdrPolicy", "explicitToneMapToSdrBt709");
        RequireBoolean(profile, "output.hdrWarningRequired", expected: true);
        RequireText(profile, "output.externalPlatformCompliance", "notClaimed");

        RequireText(profile, "editing.primaryTransition", "hardCut");
        RequireInteger(profile, "editing.hardCutDuration", 0, DefaultSettingUnit.Milliseconds);
        RequireBoolean(profile, "editing.crossfadeEnabled", expected: false);
        RequireInteger(
            profile,
            "editing.crossfadeDurationWhenEnabled",
            250,
            DefaultSettingUnit.Milliseconds);
        RequireBoolean(profile, "editing.shortOpenerEnabled", expected: false);
        RequireBoolean(profile, "editing.longOpenerEnabled", expected: false);
        RequireText(
            profile,
            "editing.shortHookPolicy",
            "sourceGroundedQuestionOrConclusionOnly");
        RequireCallToAction(profile);

        RequireText(profile, "subtitles.primaryProfile", "japaneseSdhOrientedCandidate");
        RequireText(profile, "subtitles.translationProfile", "japaneseNormalTranslation");
        RequireInteger(
            profile,
            "subtitles.readingSpeedMaximum",
            7,
            DefaultSettingUnit.CharactersPerSecond);
        RequireInteger(
            profile,
            "subtitles.horizontalLineLengthMaximum",
            16,
            DefaultSettingUnit.FullWidthCharacters);
        RequireInteger(profile, "subtitles.lineCountMaximum", 2, DefaultSettingUnit.Count);
        RequireRational(
            profile,
            "subtitles.cueMinimumDuration",
            new RationalNumber(5, 6),
            DefaultSettingUnit.Seconds);
        RequireInteger(profile, "subtitles.cueMaximumDuration", 7, DefaultSettingUnit.Seconds);
        RequireInteger(profile, "subtitles.cueGapMinimum", 2, DefaultSettingUnit.Frames);
        RequireText(profile, "subtitles.semanticCompleteness", "NOT EVALUATED");
        RequireText(profile, "subtitles.platformCompliance", "notClaimed");
        RequireText(profile, "subtitles.platformUiMaskStatus", "unmeasured");
        RequireText(
            profile,
            "subtitles.platformUiMaskFallback",
            "bbcGenericSubtitleRegion");
        RequireBoolean(profile, "subtitles.platformUiMaskWarningRequired", expected: true);

        RequireText(profile, "audio.quickStartLoudnessProfile", "internalOnline");
        RequireInteger(
            profile,
            "audio.internalOnlineTargetLoudness",
            -16,
            DefaultSettingUnit.LoudnessUnitsFullScale);
        RequireInteger(
            profile,
            "audio.internalOnlineLoudnessTolerance",
            1,
            DefaultSettingUnit.Decibels);
        RequireInteger(
            profile,
            "audio.truePeakMaximum",
            -1,
            DefaultSettingUnit.DecibelsFullScale);
        RequireInteger(
            profile,
            "audio.broadcastTargetLoudness",
            -23,
            DefaultSettingUnit.LoudnessUnitsFullScale);
        RequireBoolean(profile, "audio.dialogueBackgroundMusicEnabled", expected: false);
        RequireText(profile, "audio.musicalFitEvaluation", "NOT EVALUATED");
        RequireText(profile, "audio.perceptualSpeechUnderstanding", "NOT EVALUATED");

        RequireText(profile, "assets.musicUsageScope", "internalOnly");
        RequireBoolean(profile, "assets.musicLoopEnabled", expected: false);
        RequireText(profile, "project.publicationScope", "internalOnly");
        RequireText(profile, "project.template", "builtInNeutralNoLogo");
        RequireInteger(profile, "project.requiredSourceVideoCount", 1, DefaultSettingUnit.Count);
        RequireInteger(profile, "project.verticalShortCandidateCount", 3, DefaultSettingUnit.Count);
        RequireInteger(profile, "thumbnails.candidateCount", 3, DefaultSettingUnit.Count);
        RequireBoolean(profile, "thumbnails.generatedFaceEnabled", expected: false);
        RequireBoolean(profile, "thumbnails.generatedSceneEnabled", expected: false);
        RequireBoolean(profile, "thumbnails.sourceFrameOnly", expected: true);
        RequireBoolean(profile, "privacy.cloudTextSuggestionEnabled", expected: false);
        RequireBoolean(profile, "privacy.externalPublicationEnabled", expected: false);
        RequireBoolean(profile, "storage.autoDeleteEnabled", expected: false);
        RequireBoolean(profile, "evaluation.humanPassClaimEnabled", expected: false);
    }

    private static void RequireCallToAction(DefaultSettingsProfile profile)
    {
        DefaultSettingDefinition definition = profile.GetDefinition("editing.callToActionTexts");
        if (definition.Group != DefaultSettingGroup.Editing ||
            definition.IsAdvanced ||
            !string.Equals(definition.DisplayName, "Call to action", StringComparison.Ordinal) ||
            !string.Equals(
                definition.Description,
                CallToActionDescription,
                StringComparison.Ordinal))
        {
            throw new JsonException("The bundled call-to-action policy is not approved.");
        }

        RequireTextList(profile, definition.Key, ["本編を見る"]);
    }

    private static void RequireBoolean(
        DefaultSettingsProfile profile,
        string key,
        bool expected)
    {
        DefaultSettingDefinition definition = profile.GetDefinition(key);
        if (definition.DefaultValue.Kind != DefaultSettingValueKind.Boolean ||
            definition.DefaultValue.BooleanValue != expected ||
            definition.Unit != DefaultSettingUnit.None)
        {
            throw new JsonException($"The bundled value for '{key}' is not approved.");
        }
    }

    private static void RequireInteger(
        DefaultSettingsProfile profile,
        string key,
        long expected,
        DefaultSettingUnit expectedUnit)
    {
        DefaultSettingDefinition definition = profile.GetDefinition(key);
        if (definition.DefaultValue.Kind != DefaultSettingValueKind.Integer ||
            definition.DefaultValue.IntegerValue != expected ||
            definition.Unit != expectedUnit)
        {
            throw new JsonException($"The bundled value for '{key}' is not approved.");
        }
    }

    private static void RequireRational(
        DefaultSettingsProfile profile,
        string key,
        RationalNumber expected,
        DefaultSettingUnit expectedUnit)
    {
        DefaultSettingDefinition definition = profile.GetDefinition(key);
        if (definition.DefaultValue.Kind != DefaultSettingValueKind.Rational ||
            definition.DefaultValue.RationalValue != expected ||
            definition.Unit != expectedUnit)
        {
            throw new JsonException($"The bundled value for '{key}' is not approved.");
        }
    }

    private static void RequireText(
        DefaultSettingsProfile profile,
        string key,
        string expected)
    {
        DefaultSettingDefinition definition = profile.GetDefinition(key);
        if (definition.DefaultValue.Kind != DefaultSettingValueKind.Text ||
            !string.Equals(
                definition.DefaultValue.TextValue,
                expected,
                StringComparison.Ordinal) ||
            definition.Unit != DefaultSettingUnit.None)
        {
            throw new JsonException($"The bundled value for '{key}' is not approved.");
        }
    }

    private static void RequireTextList(
        DefaultSettingsProfile profile,
        string key,
        IReadOnlyList<string> expected)
    {
        DefaultSettingDefinition definition = profile.GetDefinition(key);
        if (definition.DefaultValue.Kind != DefaultSettingValueKind.TextList ||
            !definition.DefaultValue.TextListValue.SequenceEqual(
                expected,
                StringComparer.Ordinal) ||
            definition.Unit != DefaultSettingUnit.None)
        {
            throw new JsonException($"The bundled value for '{key}' is not approved.");
        }
    }
}
